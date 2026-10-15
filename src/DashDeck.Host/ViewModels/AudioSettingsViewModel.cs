using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Settings;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Settings ▸ AUDIO: where the music comes from, and whether playback gets out of the way of
/// spoken instructions (Q14, ADR-0060).
/// </summary>
/// <remarks>
/// This section also carried the output delay, the video sync offset and the microphone
/// measurement that fed them (ADR-0059). All of it is parked on <c>parked/audio-delay</c>: the
/// offset only ever reached DashDeck's own player, and films are watched in a hosted application
/// that nothing here can delay (Q39). What is left is the half that works.
/// </remarks>
public sealed partial class AudioSettingsViewModel(DisplaySettings settings) : ObservableObject
{
    private readonly DisplaySettings _settings = settings;

    /// <summary>Where the Stereo card reads music from.</summary>
    public string MusicFolder
    {
        get => _settings.MusicFolder;
        set
        {
            _settings.MusicFolder = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>Whether playback gets out of the way of spoken instructions (Q14).</summary>
    public bool DuckUnderInstructions
    {
        get => _settings.DuckUnderInstructions;
        set
        {
            _settings.DuckUnderInstructions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DuckLabel));
        }
    }

    /// <inheritdoc cref="DuckUnderInstructions"/>
    public string DuckLabel => DuckUnderInstructions ? "ON" : "OFF";

    /// <summary>Turn ducking on or off.</summary>
    [RelayCommand]
    private void ToggleDuck() => DuckUnderInstructions = !DuckUnderInstructions;

    /// <summary>
    /// Re-read what the section shows. Kept because the rail refreshes a section on the way in.
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        OnPropertyChanged(nameof(MusicFolder));
        OnPropertyChanged(nameof(DuckUnderInstructions));
        OnPropertyChanged(nameof(DuckLabel));
    }
}
