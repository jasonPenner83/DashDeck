using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Host.Settings;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Settings ▸ AUDIO: whether DashDeck's own playback gets out of the way of spoken instructions
/// (Q14, ADR-0060).
/// </summary>
/// <remarks>
/// This section has lost two things to parked branches, and what is left is one switch. The
/// output delay, the video sync offset and the microphone measurement are on
/// <c>parked/audio-delay</c> (Q39); the music folder went with the Stereo panel to
/// <c>parked/stereo-music</c>, because walking that folder — a network share — was the last of
/// the four things holding the window shut at launch (Q41).
///
/// The ducking switch stays because it is a policy, not a player: it is tested, it is Q14's owed
/// contract, and it costs nothing to keep.
/// </remarks>
public sealed partial class AudioSettingsViewModel(DisplaySettings settings) : ObservableObject
{
    private readonly DisplaySettings _settings = settings;

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
        OnPropertyChanged(nameof(DuckUnderInstructions));
        OnPropertyChanged(nameof(DuckLabel));
    }
}
