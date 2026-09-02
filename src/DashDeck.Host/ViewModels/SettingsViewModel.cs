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

/// <summary>
/// Settings. Appearance for now — the part that had a reason to exist first.
/// </summary>
/// <remarks>
/// What is adjustable here is deliberately narrow (B1). The palette is a token set, but
/// exposing every token as a colour picker would let someone quietly break the one rule the
/// dash is strictest about: that a value's quality is always legible. So the choices are a
/// day/night mode and an accent — presets, or any colour that passes
/// <see cref="AccentValidation"/> — and the four quality colours are not on offer at all.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;

    public SettingsViewModel(ThemeService theme)
    {
        _theme = theme;

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
