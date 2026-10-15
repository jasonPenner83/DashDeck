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
    private readonly Func<(StereoPlayer? Player, string? Problem)> _makePlayer;
    private readonly DisplaySettings _settings;
    private readonly DispatcherTimer _tick;

    private StereoPlayer? _player;
    private string? _unavailable;
    private bool _made;
    private bool _scanned;
    private bool _seeking;

    /// <param name="makePlayer">
    /// Builds the player, called at most once and not until something actually needs it.
    /// </param>
    /// <param name="settings">Where the music folder is kept.</param>
    /// <remarks>
    /// <b>Built on demand, because building it is expensive.</b> The player is LibVLC, and
    /// <c>new LibVLC()</c> walks VLC's plugin folder — some seventeen hundred files — to build its
    /// cache. Doing that in this constructor put it on the startup path, before the window was
    /// drawn, where it cost seconds on a cold tablet and looked exactly like a dash that would not
    /// start (Q41). Nothing about the stereo needs to exist until someone asks for music.
    /// </remarks>
    public StereoViewModel(
        Func<(StereoPlayer? Player, string? Problem)> makePlayer,
        DisplaySettings settings)
    {
        _makePlayer = makePlayer;
        _settings = settings;

        Volume = 70;

        // A second is enough for a position bar and cheap enough not to think about; the clock
        // elsewhere in the shell runs the same way.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
    }

    /// <summary>
    /// Whether the folder has been read yet, so the panel can say what it is waiting for.
    /// </summary>
    public bool Scanned => _scanned;

    /// <summary>
    /// The player, built the first time it is wanted. Null when LibVLC would not load.
    /// </summary>
    /// <remarks>
    /// Every path that reaches this is a person asking for something — pressing PLAY, opening the
    /// panel, scanning the folder. The one-second tick deliberately does <em>not</em>: it reads
    /// <see cref="_player"/> directly, so a dash that nobody asks for music on never pays for
    /// LibVLC at all.
    /// </remarks>
    private StereoPlayer? Player
    {
        get
        {
            if (_made)
            {
                return _player;
            }

            _made = true;
            (_player, _unavailable) = _makePlayer();

            if (_player is not null)
            {
                _player.Changed += OnPlayerChanged;
                _player.Volume = Volume;
            }

            OnPropertyChanged(nameof(HasProblem));
            OnPropertyChanged(nameof(HasTracks));

            return _player;
        }
    }

    /// <summary>The queue.</summary>
    public ObservableCollection<TrackRow> Tracks { get; } = [];

    /// <summary>Where the music is read from.</summary>
    public string Folder => _settings.MusicFolder;

    /// <summary>What is playing, or an invitation to choose a folder.</summary>
    [ObservableProperty]
    private string _title = "Press RESCAN to read the music folder";

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
        if (Player is not null)
        {
            Player.Volume = value;
        }
    }

    partial void OnProblemChanged(string value)
    {
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasTracks));
    }

    /// <summary>
    /// Read the music folder. <b>Nothing calls this on its own</b> — only the RESCAN button does.
    /// </summary>
    /// <remarks>
    /// It used to run from this view model's constructor, which the shell builds while starting
    /// up, and the owner's music folder is a large **network share**: both the existence check and
    /// the recursive walk sit for as long as SMB takes, so the window did not appear until the
    /// walk finished (Q41). Deferring it to the first time the panel opened was not enough —
    /// opening the panel in a truck, where the share is unreachable, would have the same shape. So
    /// the dash now reads that folder only when somebody asks it to, and says so until they do.
    /// </remarks>
    [RelayCommand]
    public void Rescan()
    {
        _scanned = true;
        Tracks.Clear();

        var folder = _settings.MusicFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            // No player is built for this: there is nothing to play, and the message is the same
            // whether or not LibVLC would have loaded.
            Problem = "No music folder chosen. Settings ▸ AUDIO ▸ MUSIC FOLDER.";
            _player?.SetQueue([]);
            Refresh();
            return;
        }

        // Everything that touches the folder happens off the UI thread, including whether it is
        // there at all: this one is a **network share**, and both Directory.Exists and the walk
        // can sit for a minute on an SMB path that is slow, asleep or gone. The panel says what
        // it is doing and the dash stays live throughout.
        Problem = "";
        Title = "Reading the music folder…";

        _ = ScanAsync(folder);
    }

    private async Task ScanAsync(string folder)
    {
        IReadOnlyList<Track> tracks;

        try
        {
            tracks = await Task.Run(() =>
            {
                if (!Directory.Exists(folder))
                {
                    throw new DirectoryNotFoundException(
                        $"{folder} cannot be reached. If it is a network share, check it is "
                            + "connected; otherwise choose another folder in Settings ▸ AUDIO.");
                }

                return MusicLibrary.From(
                    Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
            }).ConfigureAwait(true);
        }
        catch (DirectoryNotFoundException ex)
        {
            Problem = ex.Message;
            _player?.SetQueue([]);
            Refresh();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = $"Could not read {folder}: {ex.Message}";
            _player?.SetQueue([]);
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

        // Here, and only here, is a player actually needed — there are tracks to hand over. This
        // is the line that pays for LibVLC, the first time a folder with music in it is scanned.
        if (Player is null)
        {
            Problem = _unavailable ?? "Audio playback is not available on this machine.";
            Refresh();
            return;
        }

        Player.SetQueue(tracks);
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(HasTracks));
        Refresh();
    }

    /// <summary>Play or pause.</summary>
    [RelayCommand]
    private void TogglePlay() => Player?.TogglePlay();

    /// <summary>Next track.</summary>
    [RelayCommand]
    private void Next() => Player?.Next();

    /// <summary>Previous track, or restart this one — <see cref="MusicLibrary.PreviousFrom"/> decides.</summary>
    [RelayCommand]
    private void Previous() => Player?.Previous();

    /// <summary>Play a track picked from the list.</summary>
    [RelayCommand]
    private void PlayTrack(TrackRow? row)
    {
        if (row is not null)
        {
            Player?.GoTo(row.Index);
        }
    }

    /// <summary>Seek to a fraction of the current track. Bound to the bar being dragged.</summary>
    public void SeekToFraction(double fraction)
    {
        if (Player is null || Player.Length <= TimeSpan.Zero)
        {
            return;
        }

        _seeking = true;
        Player.SeekTo(Player.Length * Math.Clamp(fraction, 0, 1));
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
        // The field, never the property: this runs every second, and building LibVLC from a
        // timer would put back exactly the cost that was moved off the startup path.
        if (_player is null)
        {
            Title = _made
                ? "No audio playback"
                : _scanned ? "Nothing queued" : "Press RESCAN to read the music folder";

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
