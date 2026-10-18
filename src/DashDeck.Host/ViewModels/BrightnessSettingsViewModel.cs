using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Core.Brightness;
using DashDeck.Host.Brightness;
using DashDeck.Host.Settings;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Settings ▸ DISPLAY ▸ BRIGHTNESS: whether DashDeck sets the backlight, and to what by day and by
/// night (ADR-0068).
/// </summary>
/// <remarks>
/// Day and night themselves are chosen in Appearance, because the palette and the backlight follow
/// the same answer; this section only says how bright each one is.
/// </remarks>
public sealed partial class BrightnessSettingsViewModel : ObservableObject
{
    private readonly DisplaySettings _settings;
    private readonly ScreenBrightness? _brightness;

    public BrightnessSettingsViewModel(DisplaySettings settings, ScreenBrightness? brightness)
    {
        _settings = settings;
        _brightness = brightness;

        DayLevels = [.. BrightnessPolicy.DayChoices.Select(p => new BrightnessLevelOption(p))];
        NightLevels = [.. BrightnessPolicy.NightChoices.Select(p => new BrightnessLevelOption(p))];
        Sync();

        if (brightness is not null)
        {
            brightness.PropertyChanged += OnBrightnessChanged;
        }
    }

    /// <summary>The levels offered by day.</summary>
    public IReadOnlyList<BrightnessLevelOption> DayLevels { get; }

    /// <summary>The levels offered by night.</summary>
    public IReadOnlyList<BrightnessLevelOption> NightLevels { get; }

    /// <summary>ON/OFF for the switch.</summary>
    public string EnabledLabel => _settings.BrightnessEnabled ? "ON" : "OFF";

    /// <summary>Whether the level rows mean anything.</summary>
    public bool IsEnabled => _settings.BrightnessEnabled;

    /// <summary>What the backlight is doing now.</summary>
    public string Status => _brightness?.Status ?? "Backlight control is not running in this launch.";

    /// <summary>Turn backlight control on or off. Off gives the tablet its own level back at once.</summary>
    [RelayCommand]
    private void ToggleEnabled()
    {
        _settings.BrightnessEnabled = !_settings.BrightnessEnabled;
        Changed();
    }

    /// <summary>Choose the day level.</summary>
    [RelayCommand]
    private void SetDay(BrightnessLevelOption? option)
    {
        if (option is not null)
        {
            _settings.DayBrightness = option.Percent;
            Changed();
        }
    }

    /// <summary>Choose the night level.</summary>
    [RelayCommand]
    private void SetNight(BrightnessLevelOption? option)
    {
        if (option is not null)
        {
            _settings.NightBrightness = option.Percent;
            Changed();
        }
    }

    // Applied now rather than at the next beat, so a tap is seen as it is made.
    private void Changed()
    {
        Sync();
        _brightness?.Reapply();
    }

    private void Sync()
    {
        foreach (var option in DayLevels)
        {
            option.IsSelected = option.Percent == _settings.DayBrightness;
        }

        foreach (var option in NightLevels)
        {
            option.IsSelected = option.Percent == _settings.NightBrightness;
        }

        OnPropertyChanged(nameof(EnabledLabel));
        OnPropertyChanged(nameof(IsEnabled));
    }

    private void OnBrightnessChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenBrightness.Status))
        {
            OnPropertyChanged(nameof(Status));
        }
    }
}

/// <summary>One brightness level, as a chip.</summary>
public sealed partial class BrightnessLevelOption : ObservableObject
{
    public BrightnessLevelOption(int percent) => Percent = percent;

    /// <summary>The level, per cent.</summary>
    public int Percent { get; }

    /// <summary>What the chip says.</summary>
    public string Label => $"{Percent}%";

    [ObservableProperty]
    private bool _isSelected;
}
