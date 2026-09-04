using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Converters;
using DashDeck.Host.Settings;
using DashDeck.Host.Theme;

namespace DashDeck.Host.ViewModels;

/// <summary>One of Day, Night or Auto.</summary>
public sealed partial class ThemeModeOption(string label, string detail, ThemeMode mode) : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public string Label { get; } = label;

    public string Detail { get; } = detail;

    public ThemeMode Mode { get; } = mode;
}

/// <summary>One choosable accent, with a swatch to show it.</summary>
public sealed partial class AccentSwatch(AccentOption option) : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public AccentOption Option { get; } = option;

    public string Label { get; } = option.Name;

    public Brush Swatch { get; } = new SolidColorBrush(option.Colour);
}

/// <summary>One tab in the settings rail.</summary>
public sealed partial class SettingsSection(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Settings, in sections: appearance, mount, display and diagnostics.
/// </summary>
/// <remarks>
/// One long scroll became a rail and a pane once there was more than one thing to adjust —
/// the sections answer different questions and are reached for at different times.
/// <para>
/// What is adjustable is still deliberately narrow (B1). The palette is a token set, but
/// exposing every token as a colour picker would let someone quietly break the one rule the
/// dash is strictest about: that a value's quality is always legible. So appearance offers a
/// day/night mode and an accent — presets, or any colour that passes
/// <see cref="AccentValidation"/> — and the four quality colours are not on offer at all.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly DashDeck.Host.Sensors.SensorService _sensors;

    /// <summary>
    /// The settings tabs. One long scroll became a rail and a pane once the sections stopped
    /// fitting a glance � appearance, mount, display and diagnostics answer different questions
    /// and are reached for at different times.
    /// </summary>
    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new SettingsSection("APPEARANCE") { IsSelected = true },
        new SettingsSection("MOUNT"),
        new SettingsSection("DISPLAY"),
        new SettingsSection("DIAGNOSTICS"),
    ];

    /// <summary>Which section is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearance))]
    [NotifyPropertyChangedFor(nameof(IsMount))]
    [NotifyPropertyChangedFor(nameof(IsDisplay))]
    [NotifyPropertyChangedFor(nameof(IsDiagnostics))]
    private string _section = "APPEARANCE";

    public bool IsAppearance => Section == "APPEARANCE";
    public bool IsMount => Section == "MOUNT";
    public bool IsDisplay => Section == "DISPLAY";
    public bool IsDiagnostics => Section == "DIAGNOSTICS";

    /// <summary>Switch sections. Bound to the rail.</summary>
    [RelayCommand]
    private void SelectSection(SettingsSection? section)
    {
        if (section is null)
        {
            return;
        }

        Section = section.Name;

        foreach (var candidate in Sections)
        {
            candidate.IsSelected = ReferenceEquals(candidate, section);
        }
    }

    public SettingsViewModel(
        ThemeService theme,
        DashDeck.Host.Settings.DisplaySettings display,
        DashDeck.Host.Sensors.SensorService sensors)
    {
        _theme = theme;
        Display = display;
        _sensors = sensors;

        WebScales = [.. DashDeck.Host.Settings.DisplaySettings.Choices.Select(s => new WebScaleOption(s))];
        display.PropertyChanged += (_, _) => SyncScales();
        SyncScales();

        Modes =
        [
            new ThemeModeOption("DAY", "Full brightness", ThemeMode.Day),
            new ThemeModeOption("NIGHT", "Dimmed", ThemeMode.Night),
            new ThemeModeOption("AUTO", "Decide for me", ThemeMode.Auto),
        ];

        Accents = [.. AccentOption.All.Select(a => new AccentSwatch(a))];

        // Start the field on whatever is in use, so editing an accent begins from it rather
        // than from an empty box.
        _customAccentHex = ThemeService.ToHex(theme.Accent.Colour);

        theme.PropertyChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>True once the tablet''s mount has been levelled.</summary>
    public bool IsLevelled => _sensors.IsLevelled;

    /// <summary>What the level control says, and what it means.</summary>
    public string LevelCaption => IsLevelled ? "RE-LEVEL THE MOUNT" : "LEVEL THE MOUNT";

    /// <summary>When the current reference was taken, or why there is none.</summary>
    public string LevelDetail => _sensors.Reference.CapturedUtc is { } at
        ? $"Levelled {at.ToLocalTime():d MMM, HH:mm}. Re-level if the mount has moved."
        : "Not levelled. Pitch, roll and G will not render until it is.";

    /// <summary>
    /// Capture the tablet''s orientation as level and forward.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on the compass because it is a calibration done once, parked, on
    /// flat ground � not something reached for while driving. It was on the stage''s action bar
    /// until that row cost more picture than it was worth (ADR-0022).
    /// </remarks>
    [RelayCommand]
    private void Level()
    {
        _sensors.Level();
        OnPropertyChanged(nameof(IsLevelled));
        OnPropertyChanged(nameof(LevelCaption));
        OnPropertyChanged(nameof(LevelDetail));
    }

    /// <summary>How large web occupants render. Shared with them, so a change applies live.</summary>
    public DashDeck.Host.Settings.DisplaySettings Display { get; }

    /// <summary>The scales offered, as chips.</summary>
    public IReadOnlyList<WebScaleOption> WebScales { get; } = [];

    /// <summary>Pick a scale for the web occupants.</summary>
    [RelayCommand]
    private void SetWebScale(WebScaleOption? option)
    {
        if (option is not null)
        {
            Display.WebScale = option.Scale;
        }
    }

    private void SyncScales()
    {
        foreach (var option in WebScales)
        {
            // Compared with a tolerance, because these round-trip through JSON as doubles
            // and 0.7 does not always come back as 0.7.
            option.IsSelected = Math.Abs(option.Scale - Display.WebScale) < 0.001;
        }

        OnPropertyChanged(nameof(Display));
    }

    /// <summary>Day, Night, Auto.</summary>
    public IReadOnlyList<ThemeModeOption> Modes { get; }

    /// <summary>The presets. Not the only option — see <see cref="CustomAccentHex"/>.</summary>
    public IReadOnlyList<AccentSwatch> Accents { get; }

    /// <summary>
    /// A hand-typed accent, as <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Checked on every keystroke rather than on apply, so the answer arrives while the
    /// colour is still being chosen instead of after committing to it.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomPreview))]
    [NotifyPropertyChangedFor(nameof(CustomMessage))]
    [NotifyPropertyChangedFor(nameof(CustomIsRejected))]
    [NotifyPropertyChangedFor(nameof(CustomMessageBrush))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomAccentCommand))]
    private string _customAccentHex;

    /// <summary>What the typed colour looks like — the point of checking as you type.</summary>
    public Brush CustomPreview => AccentValidation.TryParse(CustomAccentHex, out var colour)
        ? new SolidColorBrush(colour)
        : Brushes.Transparent;

    /// <summary>Why the typed colour cannot be used, or how to type one.</summary>
    public string CustomMessage
    {
        get
        {
            if (!AccentValidation.TryParse(CustomAccentHex, out var colour))
            {
                return "Six hex digits, like #FF7A1A.";
            }

            var check = AccentValidation.Check(colour);
            return check.IsUsable ? "Looks good." : check.Message;
        }
    }

    /// <summary>True when the message is a refusal, so it can be shown in the fault colour.</summary>
    public bool CustomIsRejected =>
        AccentValidation.TryParse(CustomAccentHex, out var colour) && !AccentValidation.Check(colour).IsUsable;

    /// <summary>Fault red while the typed colour is refused, faint otherwise.</summary>
    /// <remarks>
    /// Read from <see cref="QualityPalette"/> rather than declared again in XAML, so the red
    /// that says no here is the same red the dash uses for a fault, permanently.
    /// </remarks>
    public Brush CustomMessageBrush => CustomIsRejected
        ? QualityPalette.Fault
        : (Brush)System.Windows.Application.Current.Resources["TextFaintBrush"];

    /// <summary>Where the choice is written. Shown because a setting you cannot find is a setting you cannot back up.</summary>
    public string SettingsPath => SettingsStore.Path;

    /// <summary>What Auto is currently deciding from, shown only while Auto is selected.</summary>
    public string AutoSource => _theme.AutoSource;

    /// <summary>True while Auto is selected, so the explanation can be hidden otherwise.</summary>
    public bool IsAuto => _theme.Mode is ThemeMode.Auto;

    /// <summary>Which palette is actually in use right now, Auto included.</summary>
    public string ResolvedTheme => _theme.IsNight ? "Night" : "Day";

    [RelayCommand]
    private void SetMode(ThemeModeOption? option)
    {
        if (option is not null)
        {
            _theme.Mode = option.Mode;
        }
    }

    [RelayCommand]
    private void SetAccent(AccentSwatch? swatch)
    {
        if (swatch is not null)
        {
            _theme.Accent = swatch.Option;
            CustomAccentHex = ThemeService.ToHex(swatch.Option.Colour);
        }
    }

    /// <summary>Whether the typed colour is currently applicable. Drives the button's enabled state.</summary>
    private bool CanApplyCustomAccent() =>
        AccentValidation.TryParse(CustomAccentHex, out var colour) && AccentValidation.Check(colour).IsUsable;

    [RelayCommand(CanExecute = nameof(CanApplyCustomAccent))]
    private void ApplyCustomAccent()
    {
        if (AccentValidation.TryParse(CustomAccentHex, out var colour))
        {
            _theme.Accent = new AccentOption("CUSTOM", colour);
        }
    }

    private void Sync()
    {
        foreach (var mode in Modes)
        {
            mode.IsCurrent = mode.Mode == _theme.Mode;
        }

        foreach (var accent in Accents)
        {
            accent.IsCurrent = accent.Option == _theme.Accent;
        }

        OnPropertyChanged(nameof(AutoSource));
        OnPropertyChanged(nameof(IsAuto));
        OnPropertyChanged(nameof(ResolvedTheme));
        OnPropertyChanged(nameof(CustomMessageBrush));
    }
}

/// <summary>One web-scale chip.</summary>
/// <remarks>
/// Labelled as a percentage because that is how every browser has ever expressed zoom, and a
/// dash is not the place to teach somebody a new unit.
/// </remarks>
public sealed partial class WebScaleOption : ObservableObject
{
    public WebScaleOption(double scale) => Scale = scale;

    /// <summary>The multiplier handed to WebView2.</summary>
    public double Scale { get; }

    /// <summary>What the chip says.</summary>
    public string Label => $"{Scale * 100:0}%";

    [ObservableProperty]
    private bool _isSelected;
}

