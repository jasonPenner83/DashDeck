using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Core.Media;
using DashDeck.Host.Audio;
using DashDeck.Host.Settings;

namespace DashDeck.Host.ViewModels;

/// <summary>One queued track, for the list on the panel.</summary>
public sealed partial class TrackRow(Track track, int index) : ObservableObject
{
    [ObservableProperty]
    private bool _isPlaying;

    public Track Track { get; } = track;

    public int Index { get; } = index;

    public string Title => Track.Title;

    public string Folder => Track.Folder;
}

/// <summary>
/// The Stereo panel: DashDeck's own playback, on the bottom third (Q14).
/// </summary>
/// <remarks>
/// <b>Additive, and nothing is taken over (C3).</b> This is the tablet's player, pointed at the
/// output the person chose in Windows — which, on a truck with no AUX jack (Q33), is the Bluetooth
/// link to SYNC 3. The truck's own radio is untouched and unreachable; DashDeck sends nothing to it.
/// <para>
/// <b>No delay compensation here, by design.</b> Music has nothing to be in sync with, so the
/// 150-250 ms the link adds is inaudible for playback — which is the reason this panel survived
/// when the delay machinery around it was parked (Q39, <c>parked/audio-delay</c>).
/// </para>
/// </remarks>
public sealed partial class StereoViewModel : ObservableObject, IDisposable
{
    private readonly StereoPlayer? _player;
    private readonly DisplaySettings _settings;
    private readonly DispatcherTimer _tick;
    private readonly string? _unavailable;
    private bool _seeking;

    /// <param name="player">The player, or null when LibVLC would not load.</param>
    /// <param name="settings">Where the music folder and the delay are kept.</param>
    /// <param name="unavailable">Why there is no player, when there is none.</param>
    public StereoViewModel(StereoPlayer? player, DisplaySettings settings, string? unavailable = null)
    {
        _player = player;
        _settings = settings;
        _unavailable = unavailable;

        Volume = player?.Volume ?? 70;

        if (_player is not null)
        {
            _player.Changed += OnPlayerChanged;
        }

        // A second is enough for a position bar and cheap enough not to think about; the clock
        // elsewhere in the shell runs the same way.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        Rescan();
    }

    /// <summary>The queue.</summary>
    public ObservableCollection<TrackRow> Tracks { get; } = [];

    /// <summary>Where the music is read from.</summary>
    public string Folder => _settings.MusicFolder;

    /// <summary>What is playing, or an invitation to choose a folder.</summary>
    [ObservableProperty]
    private string _title = "Nothing queued";

    /// <summary>The album, which is the folder's name until tags are read.</summary>
    [ObservableProperty]
    private string _album = "";

    /// <summary>Elapsed and total, as the panel shows them.</summary>
    [ObservableProperty]
    private string _elapsed = "0:00";

    /// <inheritdoc cref="Elapsed"/>
    [ObservableProperty]
    private string _duration = "0:00";

    /// <summary>Position as a fraction, for the bar.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>PLAY or PAUSE, for the button's face.</summary>
    [ObservableProperty]
    private string _playLabel = "PLAY";

    /// <summary>Volume 0-100, what the person chose before any ducking.</summary>
    [ObservableProperty]
    private int _volume;

    /// <summary>Why the music is quieter or stopped, or empty when nothing else wants the output.</summary>
    [ObservableProperty]
    private string _duckReason = "";

    /// <summary>Whether something else is claiming the speakers right now.</summary>
    [ObservableProperty]
    private bool _isDucked;

    /// <summary>Why there is nothing to play, or empty when there is.</summary>
    [ObservableProperty]
    private string _problem = "";

    /// <summary>Whether <see cref="Problem"/> has something to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Whether there is a queue to show. The list and the problem swap places.</summary>
    public bool HasTracks => !HasProblem && Tracks.Count > 0;

    partial void OnVolumeChanged(int value)
    {
        if (_player is not null)
        {
            _player.Volume = value;
        }
    }

    partial void OnProblemChanged(string value)
    {
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasTracks));
    }

    /// <summary>Re-read the music folder.</summary>
    [RelayCommand]
    public void Rescan()
    {
        Tracks.Clear();

        if (_player is null)
        {
            Problem = _unavailable ?? "Audio playback is not available on this machine.";
            Refresh();
            return;
        }

        var folder = _settings.MusicFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            Problem = "No music folder chosen. Settings ▸ AUDIO ▸ MUSIC FOLDER.";
            _player.SetQueue([]);
            Refresh();
            return;
        }

        if (!Directory.Exists(folder))
        {
            Problem = $"{folder} is not there any more. Choose another in Settings ▸ AUDIO.";
            _player.SetQueue([]);
            Refresh();
            return;
        }

        IReadOnlyList<Track> tracks;
        try
        {
            tracks = MusicLibrary.From(
                Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = $"Could not read {folder}: {ex.Message}";
            _player.SetQueue([]);
            Refresh();
            return;
        }

        Problem = tracks.Count == 0
            ? $"Nothing playable under {folder}. Looked for {string.Join(", ", MusicLibrary.Extensions)}."
            : "";

        for (var i = 0; i < tracks.Count; i++)
        {
            Tracks.Add(new TrackRow(tracks[i], i));
        }

        _player.SetQueue(tracks);
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(HasTracks));
        Refresh();
    }

    /// <summary>Play or pause.</summary>
    [RelayCommand]
    private void TogglePlay() => _player?.TogglePlay();

    /// <summary>Next track.</summary>
    [RelayCommand]
    private void Next() => _player?.Next();

    /// <summary>Previous track, or restart this one — <see cref="MusicLibrary.PreviousFrom"/> decides.</summary>
    [RelayCommand]
    private void Previous() => _player?.Previous();

    /// <summary>Play a track picked from the list.</summary>
    [RelayCommand]
    private void PlayTrack(TrackRow? row)
    {
        if (row is not null)
        {
            _player?.GoTo(row.Index);
        }
    }

    /// <summary>Seek to a fraction of the current track. Bound to the bar being dragged.</summary>
    public void SeekToFraction(double fraction)
    {
        if (_player is null || _player.Length <= TimeSpan.Zero)
        {
            return;
        }

        _seeking = true;
        _player.SeekTo(_player.Length * Math.Clamp(fraction, 0, 1));
        _seeking = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tick.Stop();

        if (_player is not null)
        {
            _player.Changed -= OnPlayerChanged;
        }
    }

    private void OnPlayerChanged(object? sender, EventArgs e)
    {
        // The player raises this from VLC's own threads as well as ours.
        if (_tick.Dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _tick.Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void Refresh()
    {
        if (_player is null)
        {
            Title = "No audio playback";
            PlayLabel = "PLAY";
            return;
        }

        var current = _player.Current;
        Title = current?.Title ?? (Tracks.Count > 0 ? "Ready" : "Nothing queued");
        Album = current?.Folder ?? "";
        PlayLabel = _player.IsPlaying ? "PAUSE" : "PLAY";

        var position = _player.Position;
        var length = _player.Length;
        Elapsed = Clock(position);
        Duration = length > TimeSpan.Zero ? Clock(length) : "--:--";

        if (!_seeking)
        {
            Progress = length > TimeSpan.Zero
                ? Math.Clamp(position.TotalSeconds / length.TotalSeconds, 0, 1)
                : 0;
        }

        var duck = _player.Duck;
        IsDucked = duck.Pause || duck.VolumeScale < 1.0;
        DuckReason = IsDucked ? duck.Reason : "";

        foreach (var row in Tracks)
        {
            row.IsPlaying = current is { } track && row.Track.Path == track.Path;
        }

    }

    private static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                             : $"{time.Minutes}:{time.Seconds:00}";
}
