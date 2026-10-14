using DashDeck.Core.Audio;
using DashDeck.Core.Media;
using LibVLCSharp.Shared;

// WPF has a MediaPlayer of its own, and LibVLC's is the one that plays what people own.
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DashDeck.Host.Audio;

/// <summary>
/// DashDeck's own playback: a queue of files on the tablet, played through Windows' default output.
/// </summary>
/// <remarks>
/// <b>LibVLC, which is already here.</b> The video occupant bundles it precisely because WPF's
/// built-in player leans on whatever codecs Windows happens to have, and a clean tablet (C1) plays
/// about half of what a person owns. The same reasoning applies unchanged to audio, and it costs no
/// new dependency.
/// <para>
/// <b>Windows' default output, never a device of our choosing (C3).</b> DashDeck does not pick the
/// truck's audio; the person points Windows at the Bluetooth link, and the sound follows. That is
/// the same rule projection's audio follows (ADR-0057, F21).
/// </para>
/// <para>
/// <b>Volume has two owners.</b> What the person set, and what <see cref="DuckPolicy"/> says about
/// whatever else is claiming the speakers. They multiply rather than overwrite, so a turn
/// instruction ducking the music cannot lose the volume the person chose.
/// </para>
/// </remarks>
public sealed class StereoPlayer : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly VlcPlayer _player;
    private readonly object _gate = new();

    private IReadOnlyList<Track> _queue = [];
    private int _index = -1;
    private int _volume = 70;
    private DuckPlan _duck = DuckPolicy.For(AudioFocus.None);
    private bool _pausedByDuck;
    private bool _disposed;

    private StereoPlayer(LibVLC libVlc, VlcPlayer player)
    {
        _libVlc = libVlc;
        _player = player;
        _player.EndReached += OnEndReached;
        ApplyVolume();
    }

    /// <summary>
    /// Open a player, or report why not.
    /// </summary>
    /// <remarks>
    /// <b>Never throws, and that is the whole point of the factory.</b> Loading the native libvlc
    /// is the one thing here that can fail on a machine where it was not deployed, and the shell
    /// builds this at startup so the music survives leaving the Stereo destination. A constructor
    /// that threw would therefore take the <i>entire dash</i> down over a missing audio library —
    /// a dash that will not start is far worse than one that cannot play music, and the person in
    /// the truck cannot tell which went wrong from a window that never appeared.
    /// </remarks>
    public static StereoPlayer? TryCreate(out string? problem)
    {
        try
        {
            // Loads the native libvlc, once per process. Fully qualified because DashDeck.Core is
            // a namespace of ours and wins the short name.
            LibVLCSharp.Shared.Core.Initialize();

            var libVlc = new LibVLC();
            var player = new VlcPlayer(libVlc);

            problem = null;
            return new StereoPlayer(libVlc, player);
        }
        catch (Exception ex)
        {
            problem = $"No audio playback: LibVLC would not load ({ex.GetType().Name}: {ex.Message}).";
            return null;
        }
    }

    /// <summary>Raised when the track, the position or the playing state changed.</summary>
    public event EventHandler? Changed;

    /// <summary>What is queued, in play order.</summary>
    public IReadOnlyList<Track> Queue => _queue;

    /// <summary>Which track is current, or null when the queue is empty.</summary>
    public Track? Current => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;

    /// <summary>Whether sound is coming out.</summary>
    public bool IsPlaying => !_disposed && _player.IsPlaying;

    /// <summary>How far into the current track.</summary>
    public TimeSpan Position =>
        _player.Time > 0 ? TimeSpan.FromMilliseconds(_player.Time) : TimeSpan.Zero;

    /// <summary>How long the current track is, or zero when it is not known yet.</summary>
    public TimeSpan Length =>
        _player.Length > 0 ? TimeSpan.FromMilliseconds(_player.Length) : TimeSpan.Zero;

    /// <summary>The volume the person chose, 0 to 100, before any ducking.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            ApplyVolume();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What ducking is currently doing, for the screen to explain itself.</summary>
    public DuckPlan Duck => _duck;

    /// <summary>Replace the queue. Keeps playing the current track if it survived the change.</summary>
    public void SetQueue(IReadOnlyList<Track> tracks)
    {
        lock (_gate)
        {
            var playing = Current;
            _queue = tracks;
            _index = playing is { } track
                ? Math.Max(0, tracks.ToList().FindIndex(t => t.Path == track.Path))
                : tracks.Count > 0 ? 0 : -1;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Play, or resume.</summary>
    public void Play()
    {
        if (Current is not { } track)
        {
            return;
        }

        if (_player.Media is null || _player.State is VLCState.Ended or VLCState.Stopped or VLCState.NothingSpecial)
        {
            using var media = new Media(_libVlc, new Uri(track.Path));
            _player.Play(media);
        }
        else
        {
            _player.Play();
        }

        _pausedByDuck = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pause, leaving the position where it is.</summary>
    public void Pause()
    {
        if (_player.CanPause)
        {
            _player.Pause();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Play if stopped, pause if playing.</summary>
    public void TogglePlay()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Move to the next track and play it.</summary>
    public void Next() => GoTo(MusicLibrary.Next(_index, _queue.Count));

    /// <summary>
    /// Previous: restart the current track unless it has only just started.
    /// </summary>
    /// <remarks><see cref="MusicLibrary.PreviousFrom"/> owns the rule, and is tested.</remarks>
    public void Previous()
    {
        if (MusicLibrary.PreviousFrom(Position) == PreviousAction.Restart)
        {
            _player.Time = 0;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        GoTo(MusicLibrary.Previous(_index, _queue.Count));
    }

    /// <summary>Jump to a track by index and play it.</summary>
    public void GoTo(int index)
    {
        if (index < 0 || index >= _queue.Count)
        {
            return;
        }

        lock (_gate)
        {
            _index = index;
        }

        _player.Stop();
        using var media = new Media(_libVlc, new Uri(_queue[index].Path));
        _player.Play(media);
        _pausedByDuck = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seek within the current track.</summary>
    public void SeekTo(TimeSpan position)
    {
        if (_player.Length <= 0)
        {
            return;
        }

        _player.Time = (long)Math.Clamp(position.TotalMilliseconds, 0, _player.Length - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Tell the player what else is claiming the speakers, and let the policy decide.
    /// </summary>
    /// <remarks>
    /// A pause caused by ducking is remembered, so when the claim lifts the music resumes — but a
    /// pause the <i>person</i> made is not resumed behind their back.
    /// </remarks>
    public void SetFocus(IEnumerable<AudioFocus> claims)
    {
        var plan = DuckPolicy.For(claims);
        var wasPaused = _duck.Pause;
        _duck = plan;

        if (plan.Pause)
        {
            if (IsPlaying)
            {
                _pausedByDuck = true;
                Pause();
            }
        }
        else if (wasPaused && _pausedByDuck)
        {
            _pausedByDuck = false;
            Play();
        }

        ApplyVolume();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.EndReached -= OnEndReached;
        _player.Stop();
        _player.Dispose();
        _libVlc.Dispose();
    }

    private void ApplyVolume() =>
        _player.Volume = (int)Math.Round(Math.Clamp(_volume * _duck.VolumeScale, 0, 100));

    private void OnEndReached(object? sender, EventArgs e)
    {
        // VLC forbids calling back into the player from its own event thread, so the move to the
        // next track is queued rather than made here. Without this the player deadlocks at the
        // end of every track.
        _ = Task.Run(() =>
        {
            if (!_disposed)
            {
                Next();
            }
        });
    }
}
