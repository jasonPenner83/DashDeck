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
/// Owns the live palette: day or night, and which accent.
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
public sealed partial class ThemeService : ObservableObject
{
    private readonly IClock _clock;

    private (DateTimeOffset Sunrise, DateTimeOffset Sunset)? _daylight;

    /// <summary>When the last fetch was <em>attempted</em>. See <see cref="Reevaluate"/>.</summary>
    private DateTimeOffset _daylightAttemptedAt;

    private bool _daylightFetchInFlight;
    private readonly bool _loaded;
    private bool _suppressPersist;

    [ObservableProperty]
    private ThemeMode _mode = ThemeMode.Auto;

    [ObservableProperty]
    private AccentOption _accent = AccentOption.Ember;

    [ObservableProperty]
    private bool _isNight;

    /// <summary>
    /// What Auto is currently deciding from — shown in settings, because an automatic
    /// setting that will not say why it did something is infuriating.
    /// </summary>
    [ObservableProperty]
    private string _autoSource = "Waiting for sunrise and sunset";

    public ThemeService(IClock clock)
    {
        _clock = clock;

        // Restore before the first Apply, so the window comes up wearing the chosen theme
        // rather than flashing the default and correcting itself.
        var stored = SettingsStore.Load();

        if (Enum.TryParse<ThemeMode>(stored.ThemeMode, ignoreCase: true, out var mode))
        {
            _mode = mode;
        }

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
        _ = RefreshDaylightAsync();
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
    public void Preview(ThemeMode? mode, AccentOption? accent)
    {
        _suppressPersist = true;

        try
        {
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

    /// <summary>Sunrise and sunset move, but slowly. Once a day is plenty.</summary>
    private static readonly TimeSpan DaylightRefresh = TimeSpan.FromHours(12);

    /// <summary>
    /// How long to wait before trying again after a failed fetch.
    /// </summary>
    /// <remarks>
    /// Ten minutes, not twelve hours: an outage during startup should not leave Auto guessing
    /// until tomorrow. Not one second either — see <see cref="Reevaluate"/>.
    /// </remarks>
    private static readonly TimeSpan DaylightRetry = TimeSpan.FromMinutes(10);

    /// <summary>Re-evaluate Auto. Called on a timer by the shell, once a second.</summary>
    /// <remarks>
    /// <b>The backoff is timed from the attempt, not the success.</b> It used to be timed from
    /// the success, and the success time was left unset while the fetch was failing — so a
    /// service outage put the "is it time to refetch" test permanently true and the shell
    /// issued one request per second, indefinitely, at a free keyless API. Found during a real
    /// Open-Meteo outage, in a build that was already deployed.
    /// </remarks>
    public void Reevaluate()
    {
        if (Mode is ThemeMode.Auto)
        {
            Apply();
        }

        var due = _daylight is null ? DaylightRetry : DaylightRefresh;

        if (!_daylightFetchInFlight && _clock.UtcNow - _daylightAttemptedAt > due)
        {
            _ = RefreshDaylightAsync();
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

        SettingsStore.Save(new UserSettings
        {
            ThemeMode = Mode.ToString(),
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

        var palette = IsNight ? ThemePalette.Night : ThemePalette.Day;
        var accent = IsNight ? ThemePalette.Dim(Accent.Colour, 0.82) : Accent.Colour;

        SetBrush("CanvasBrush", palette.Canvas);
        SetBrush("SurfaceBrush", palette.Surface);
        SetBrush("RaisedBrush", palette.Raised);
        SetBrush("HairlineBrush", palette.Hairline);
        SetBrush("HairlineStrongBrush", palette.HairlineStrong);
        SetBrush("TextHighBrush", palette.TextHigh);
        SetBrush("TextMidBrush", palette.TextMid);
        SetBrush("TextLowBrush", palette.TextLow);
        SetBrush("TextFaintBrush", palette.TextFaint);

        SetBrush("AccentBrush", accent);
        SetBrush("AccentWashBrush", Color.FromArgb(0x14, accent.R, accent.G, accent.B));
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
        if (_daylight is not { } window)
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

    private async Task RefreshDaylightAsync()
    {
        // Stamped before the await and in a finally, so a failure — or a request still in
        // flight — cannot leave the caller thinking another attempt is due.
        _daylightAttemptedAt = _clock.UtcNow;
        _daylightFetchInFlight = true;

        try
        {
            var report = await Weather.FetchAsync(
                Weather.DefaultLatitude,
                Weather.DefaultLongitude,
                CancellationToken.None).ConfigureAwait(true);

            if (report?.Daylight is not { } daylight)
            {
                return;
            }

            _daylight = daylight;

            if (Mode is ThemeMode.Auto)
            {
                Apply();
            }
        }
        finally
        {
            _daylightFetchInFlight = false;
        }
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
