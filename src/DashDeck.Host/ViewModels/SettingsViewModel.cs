using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// day/night mode and a curated accent, and the four quality colours are not on offer at all.
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

        theme.PropertyChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>Day, Night, Auto.</summary>
    public IReadOnlyList<ThemeModeOption> Modes { get; }

    /// <summary>The accents on offer. Curated, not a colour picker — see the class remarks.</summary>
    public IReadOnlyList<AccentSwatch> Accents { get; }

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
    }
}
