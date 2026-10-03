using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Host.Settings;
using DashDeck.Host.Stage;

namespace DashDeck.Host.Theme;

/// <summary>How the day/night palette is chosen.</summary>
public enum ThemeMode
{
    /// <summary>Full brightness, always.</summary>
    Day,

    /// <summary>Dimmed, always.</summary>
    Night,

    /// <summary>Decided for you. See <see cref="ThemeService"/> for what actually decides it.</summary>
    Auto,
}

/// <summary>
/// Owns the live look: which theme (ADR-0036), day or night, and which accent.
/// </summary>
/// <remarks>
/// Applied by <em>replacing</em> the brushes in <c>Application.Resources</c>, which every
/// theme token in the shell reaches by <c>DynamicResource</c>. See <see cref="SetBrush"/> for
/// why the obvious alternative — mutating the existing brush and binding by
/// <c>StaticResource</c> — compiles, runs, and does nothing at all.
/// <para>
/// The choice is written out as soon as it changes; <see cref="Preview"/> is the way to set a
/// palette without remembering it.
/// </para>
/// </remarks>
public sealed partial class ThemeService : ObservableObject, ViewModels.IThemeHost
{
    private readonly IClock _clock;
    private readonly Stage.WeatherService _weather;
    private readonly bool _loaded;
    private bool _suppressPersist;

    [ObservableProperty]
    private ThemeMode _mode = ThemeMode.Auto;

    [ObservableProperty]
    private AccentOption _accent = AccentOption.Ember;

    /// <summary>The theme being worn. The built-in DashDeck look until one is chosen.</summary>
    [ObservableProperty]
    private ThemeDefinition _current = ThemeDefinition.BuiltIn;

    /// <summary>
    /// What is wrong with the current theme, one line each: values it could not use, text that
    /// will be hard to read, an accent too close to a quality colour. Never silent.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _problems = [];

    /// <summary>The shipped themes and the user's own.</summary>
    public ThemeLibrary Library { get; }

    /// <summary>Which stage layout shows: the one this theme names, or one chosen (ADR-0037).</summary>
    public Stage.Gauges.StageLayoutService Layouts { get; }

    /// <summary>Which climate panel layout shows: the theme's, or one chosen (ADR-0040).</summary>
    public Stage.Gauges.StageLayoutService ClimateLayouts { get; }

    /// <summary>Which console layout DASH shows: the theme's, or one chosen (ADR-0041).</summary>
    public Stage.Gauges.StageLayoutService ConsoleLayouts { get; }

    partial void OnCurrentChanged(ThemeDefinition value)
    {
        Layouts?.ThemeChanged();
        ClimateLayouts?.ThemeChanged();
        ConsoleLayouts?.ThemeChanged();
    }

    [ObservableProperty]
    private bool _isNight;

    /// <summary>
    /// What Auto is currently deciding from — shown in settings, because an automatic
    /// setting that will not say why it did something is infuriating.
    /// </summary>
    [ObservableProperty]
    private string _autoSource = "Waiting for sunrise and sunset";

    public ThemeService(IClock clock, Stage.WeatherService weather)
    {
        _clock = clock;
        _weather = weather;

        Library = new ThemeLibrary(CatalogPath.FindFolder("themes"), JsonFile.InLocalAppData("themes"));

        // Observes the shell's single fetch rather than running a second one. This class used
        // to fetch sunrise and sunset itself, on its own timer with its own backoff — and that
        // backoff, timed from a success that never came during an outage, issued one request
        // per second indefinitely. One fetcher, one backoff (see WeatherService).
        _weather.PropertyChanged += (_, _) =>
        {
            if (Mode is ThemeMode.Auto)
            {
                Apply();
            }
        };

        // Restore before the first Apply, so the window comes up wearing the chosen theme
        // rather than flashing the default and correcting itself.
        var stored = SettingsStore.Load();

        if (Enum.TryParse<ThemeMode>(stored.ThemeMode, ignoreCase: true, out var mode))
        {
            _mode = mode;
        }

        // A theme that has gone — deleted, or a shipped one a later build dropped — falls back
        // to the DashDeck look rather than leaving the screen undressed.
        // A theme that moved from the shipped set to the user's folder (LCARS, ADR-0043) is still the
        // one being worn.
        _current = Library.Find(stored.ThemeId) ?? Library.Find(ExtrasInstaller.Moved(stored.ThemeId)) ?? ThemeDefinition.BuiltIn;

        Layouts = new Stage.Gauges.StageLayoutService(
            new Stage.Gauges.StageLayoutLibrary(CatalogPath.FindFolder("stage"), JsonFile.InLocalAppData("stage")),
            () => Current.StageLayout,
            stored.StageLayout,
            choice => SettingsStore.Update(s => s with { StageLayout = choice }));

        // The climate panel is a layout too (ADR-0040): its own folders and canvas, the same rules.
        ClimateLayouts = new Stage.Gauges.StageLayoutService(
            new Stage.Gauges.StageLayoutLibrary(
                CatalogPath.FindFolder("climate"),
                JsonFile.InLocalAppData("climate"),
                Stage.Gauges.LayoutCanvas.Climate,
                Stage.Gauges.StageLayout.ClimateBuiltIns),
            () => Current.ClimateLayout,
            stored.ClimateLayout,
            choice => SettingsStore.Update(s => s with { ClimateLayout = choice }));

        // The console is a layout as well (ADR-0041): DASH draws it where the cards used to be.
        ConsoleLayouts = new Stage.Gauges.StageLayoutService(
            new Stage.Gauges.StageLayoutLibrary(
                CatalogPath.FindFolder("console"),
                JsonFile.InLocalAppData("console"),
                Stage.Gauges.LayoutCanvas.Console,
                Stage.Gauges.StageLayout.ConsoleBuiltIns),
            () => Current.ConsoleLayout,
            stored.ConsoleLayout,
            choice => SettingsStore.Update(s => s with { ConsoleLayout = choice }));

        // The shipped themes and layouts as files beside yours, to read and copy from.
        Library.WriteExamples();
        Layouts.Library.WriteExamples();
        ClimateLayouts.Library.WriteExamples();
        ConsoleLayouts.Library.WriteExamples();

        // Re-checked on load, not just on entry. A stored colour was validated against the
        // quality palette of whatever build wrote it; if a later build moves one of those
        // four, an accent that used to be fine can stop being fine, and falling back beats
        // honouring a stale approval.
        if (AccentValidation.TryParse(stored.AccentColour, out var colour) &&
            AccentValidation.Check(colour).IsUsable)
        {
            _accent = new AccentOption(stored.AccentName, colour);
        }

        _loaded = true;

        Apply();
    }

    /// <summary>
    /// Set the palette without remembering it.
    /// </summary>
    /// <remarks>
    /// For the <c>--theme</c> and <c>--accent</c> flags. Now that a choice is written out the
    /// moment it changes, a development flag that went through the normal setter would quietly
    /// overwrite whatever the user had actually chosen — looking at night mode once would make
    /// it permanent.
    /// </remarks>
    public void Preview(ThemeMode? mode, AccentOption? accent, ThemeDefinition? theme = null)
    {
        _suppressPersist = true;

        try
        {
            if (theme is not null)
            {
                Wear(theme);
            }

            if (mode is { } m)
            {
                Mode = m;
            }

            if (accent is { } a)
            {
                Accent = a;
            }
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    /// <summary>
    /// Re-evaluate Auto. Called on a timer by the shell, once a second.
    /// </summary>
    /// <remarks>
    /// No longer fetches anything. It used to own a timer and a backoff of its own, and that
    /// backoff — timed from a success that never arrived during an outage — issued one request
    /// per second, indefinitely, at a free keyless API. Sunrise and sunset now arrive from the
    /// shell's single WeatherService like every other forecast value.
    /// </remarks>
    public void Reevaluate()
    {
        if (Mode is ThemeMode.Auto)
        {
            Apply();
        }
    }

    partial void OnModeChanged(ThemeMode value)
    {
        Apply();
        Persist();
    }

    partial void OnAccentChanged(AccentOption value)
    {
        Apply();
        Persist();
    }

    /// <summary>
    /// Wear a theme, and take its accent with it.
    /// </summary>
    /// <remarks>
    /// The accent picker in Appearance still works on top of a theme — choosing a theme sets the
    /// accent to the theme's, and picking another afterwards overrides it until the next theme is
    /// chosen. A theme's accent passes the same check a hand-picked one does (ADR-0014): one too
    /// close to a quality colour is not worn, the DashDeck accent is, and the theme says why.
    /// </remarks>
    public void Wear(ThemeDefinition theme)
    {
        Current = theme;

        var day = ThemeResolver.Resolve(theme, night: false).Colour("accent");
        var colour = Color.FromRgb(day.R, day.G, day.B);

        Accent = AccentValidation.Check(colour).IsUsable
            ? AccentOption.All.FirstOrDefault(a => a.Colour == colour) ?? new AccentOption("THEME", colour)
            : AccentOption.Ember;

        Apply();
        Persist();
    }

    /// <summary>
    /// Read the theme folders again and re-apply — the way to see a hand edit without a restart,
    /// as Home Assistant's "reload themes" does.
    /// </summary>
    public void Reload()
    {
        Library.Reload();
        var reloaded = Library.Find(Current.Id) ?? ThemeDefinition.BuiltIn;

        // Through Wear, so an edited accent is picked up with everything else.
        Wear(reloaded);
    }

    /// <summary>
    /// Write the choice out.
    /// </summary>
    /// <remarks>
    /// Immediately, not on exit: a dash gets closed by having its power pulled, and a
    /// setting that only survives a graceful shutdown is a setting that does not survive.
    /// </remarks>
    private void Persist()
    {
        if (!_loaded || _suppressPersist)
        {
            return;
        }

        // Update, not Save. Building a whole UserSettings from these three fields was right
        // while the theme was the only thing stored here, and would wipe every other setting
        // the moment something else was — which it now is.
        SettingsStore.Update(stored => stored with
        {
            ThemeMode = Mode.ToString(),
            ThemeId = Current.Id,
            AccentName = Accent.Name,
            AccentColour = ToHex(Accent.Colour),
        });
    }

    /// <summary>A colour as <c>#RRGGBB</c> — what gets stored, and what the settings box shows.</summary>
    public static string ToHex(Color colour) =>
        $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private void Apply()
    {
        IsNight = Mode switch
        {
            ThemeMode.Day => false,
            ThemeMode.Night => true,
            _ => ResolveAuto(),
        };

        var theme = ThemeResolver.Resolve(Current, IsNight);
        var accent = IsNight ? ThemePalette.Dim(Accent.Colour, 0.82) : Accent.Colour;

        foreach (var token in ThemeTokens.All)
        {
            switch (token.Kind)
            {
                case TokenKind.Colour when token.Key is "accent":
                    SetBrush(token.ResourceKey, accent);
                    break;

                // Following the accent by default, so it follows the one actually worn — which may
                // be a hand-picked override — rather than the theme's.
                case TokenKind.Colour when token.Key is "selectedText" && !Current.Tokens.ContainsKey("selectedText"):
                    SetBrush(token.ResourceKey, accent);
                    break;

                case TokenKind.Colour:
                    var c = theme.Colour(token.Key);
                    SetBrush(token.ResourceKey, Color.FromArgb(c.A, c.R, c.G, c.B));
                    break;

                case TokenKind.Number when token.Key is "borderWidth":
                    Application.Current.Resources[token.ResourceKey] = new Thickness(theme.Number(token.Key));
                    break;

                case TokenKind.Number when token.ResourceKey.Length > 0:
                    Application.Current.Resources[token.ResourceKey] = new CornerRadius(theme.Number(token.Key));
                    break;

                case TokenKind.Font:
                    Application.Current.Resources[token.ResourceKey] = Font(theme.Fonts[token.Key], Current);
                    break;
            }
        }

        var wash = (byte)Math.Round(theme.Number("accentWash") * 255);
        SetBrush("AccentWashBrush", Color.FromArgb(wash, accent.R, accent.G, accent.B));

        Problems = Describe(theme);
    }

    /// <summary>Everything worth saying about the theme being worn, for Settings to show.</summary>
    private IReadOnlyList<string> Describe(ResolvedTheme worn)
    {
        var lines = new List<string>(worn.Problems);
        lines.AddRange(ThemeResolver.Legibility(IsNight ? ThemeResolver.Resolve(Current, night: false) : worn));

        var wanted = ThemeResolver.Resolve(Current, night: false).Colour("accent");
        var check = AccentValidation.Check(Color.FromRgb(wanted.R, wanted.G, wanted.B));
        if (!check.IsUsable)
        {
            lines.Add($"Its accent {wanted} was not used: {check.Message} Wearing EMBER instead.");
        }

        return lines;
    }

    /// <summary>
    /// A theme's font: its own font files first, from beside the theme file, then whatever the
    /// tablet has by that name, then the next name in the list.
    /// </summary>
    private static FontFamily Font(string families, ThemeDefinition theme)
    {
        if (theme.Folder is not { } folder || theme.FontFiles.Count == 0)
        {
            return new FontFamily(families);
        }

        // "./#Antonio" finds the family in the theme's own folder; the bare name after it is the
        // fallback if that file has gone.
        var names = families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = string.Join(", ", names.SelectMany(n => new[] { $"./#{n}", n }));
        var baseUri = new Uri(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        return new FontFamily(baseUri, list);
    }

    /// <summary>
    /// Decide day or night without being told.
    /// </summary>
    /// <remarks>
    /// <b>Headlights would be the right signal</b> — the driver has already made the
    /// judgement, and a tunnel or a heavy prairie storm is night as far as a screen is
    /// concerned. It is not available: lighting status is not in the legislated OBD-II set,
    /// it lives on the Ford body module over MS-CAN, and reaching it depends on PID
    /// discovery against the real truck (R2) and on what the Gateway Module lets through
    /// (R4, Q5).
    /// <para>
    /// So Auto runs on sunrise and sunset, which are already being fetched for the clock
    /// face and are right most of the time. When a headlight signal exists it becomes just
    /// another named signal and this method changes; nothing above it does.
    /// </para>
    /// </remarks>
    private bool ResolveAuto()
    {
        if (_weather.Daylight is not { } window)
        {
            // No answer yet. Day is the safer default — a dash that is too bright is
            // annoying, one that is too dim in sunlight is unreadable.
            AutoSource = "Sunrise and sunset unavailable — assuming day";
            return false;
        }

        var now = _clock.UtcNow.ToLocalTime();
        var night = now < window.Sunrise || now >= window.Sunset;

        AutoSource = string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"Sunrise {window.Sunrise:HH:mm}, sunset {window.Sunset:HH:mm}");

        return night;
    }

    /// <summary>
    /// Replace a token's brush.
    /// </summary>
    /// <remarks>
    /// Replacement rather than mutation, and the theme keys are referenced with
    /// <c>DynamicResource</c> rather than <c>StaticResource</c>, because the obvious approach
    /// silently does nothing: WPF freezes the <see cref="SolidColorBrush"/> instances in a
    /// compiled resource dictionary, so setting <c>.Color</c> on one is a no-op — and a
    /// <c>StaticResource</c> reference has already captured the old instance anyway.
    /// <para>
    /// Found by measuring pixels in a screenshot after day and night rendered identically.
    /// </para>
    /// </remarks>
    private static void SetBrush(string key, Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        Application.Current.Resources[key] = brush;
    }
}
