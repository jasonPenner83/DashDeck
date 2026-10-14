using System.Windows;
using System.Windows.Media;
using DashDeck.Core.Audio;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

// WPF has a MediaPlayer of its own — the one whose codec support is exactly why LibVLC is
// here instead. Aliased so the ambiguity cannot quietly resolve the wrong way.
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DashDeck.Host.Stage;

/// <summary>
/// Video on the stage, played by VLC's engine.
/// </summary>
/// <remarks>
/// LibVLC rather than <c>MediaElement</c>, and not a close call: WPF's built-in player goes
/// through Windows Media Player's codec set, which on a machine that is deliberately kept
/// clean (constraint C1) means half of what you actually own will not play. LibVLC brings
/// its own decoders, deploys as a folder of DLLs with nothing to install, and is LGPL — so
/// linking it into a closed application is fine.
/// <para>
/// This is the "don't reinvent the wheel" path taken literally. Writing a media player was
/// never going to be the interesting part of this project.
/// </para>
/// </remarks>
public sealed class VideoStageOccupant : IStageOccupant
{
    private readonly LibVLC _libVlc;
    private readonly VlcPlayer _player;
    private readonly string _mediaPath;
    private readonly Action<TimeSpan>? _remember;

    private TimeSpan _syncOffset;

    private VideoView? _view;
    private bool _disposed;


    /// <param name="mediaPath">The file to play.</param>
    /// <param name="syncOffset">
    /// Where to start the audio/video offset — normally the negative of the measured output delay
    /// (ADR-0059), since the link makes sound arrive late.
    /// </param>
    /// <param name="remember">Called when the offset is nudged, so it survives the next file.</param>
    public VideoStageOccupant(string mediaPath, TimeSpan syncOffset = default, Action<TimeSpan>? remember = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        _syncOffset = VideoSync.Clamp(syncOffset);
        _remember = remember;

        // Locates and loads the native libvlc, once per process. Fully qualified because
        // DashDeck.Core is a namespace of ours and wins the short name.
        LibVLCSharp.Shared.Core.Initialize();

        _libVlc = new LibVLC();
        _player = new VlcPlayer(_libVlc);
        _mediaPath = mediaPath;
    }

    /// <inheritdoc />
    public string Name => "VIDEO";

    /// <summary>True once VLC reports it is actually playing.</summary>
    public bool IsPlaying => _player.IsPlaying;

    /// <summary>
    /// What the player is actually doing, in one line.
    /// </summary>
    /// <remarks>
    /// Needed because <c>VideoView</c> renders into a child window, so the video surface is
    /// invisible to <c>RenderTargetBitmap</c> — a screenshot of the shell shows the stage as
    /// a black rectangle whether playback works or not. Asking the player directly is the
    /// only honest way to tell the difference without a camera pointed at the screen.
    /// </remarks>
    public string Describe()
    {
        var track = _player.Media?.Tracks?.FirstOrDefault(t => t.TrackType == TrackType.Video);

        var size = track is { } v
            ? $"{v.Data.Video.Width}x{v.Data.Video.Height}"
            : "unknown";

        return $"state={_player.State} playing={_player.IsPlaying} " +
               $"video={size} length={_player.Length}ms position={_player.Position:0.###} " +
               $"media={System.IO.Path.GetFileName(_mediaPath)}";
    }

    /// <inheritdoc />
    public FrameworkElement CreateView()
    {
        _view = new VideoView
        {
            MediaPlayer = _player,
            Background = Brushes.Black,
        };

        // Start on Loaded rather than now: the player needs a realised window handle to
        // render into, and it does not have one until the view is in the tree.
        _view.Loaded += OnLoaded;
        return _view;
    }

    /// <summary>
    /// Transport, as verbs.
    /// </summary>
    /// <remarks>
    /// <b>The seek bar is gone with the action bar it lived on.</b> A slider is not a verb and
    /// there is nowhere honest to put one now — it needs to be visible while you drag it,
    /// which a menu is not. Skipping in fixed steps is the closest thing that survives, and it
    /// is arguably the better control in a moving vehicle anyway: a thirty-second jump can be
    /// hit without looking, and a slider cannot.
    /// <para>
    /// Play and pause is one item rather than two, and its caption follows the player — a
    /// button that says PAUSE while paused is worse than no label at all.
    /// </para>
    /// </remarks>
    public IReadOnlyList<StageAction> Actions =>
    [
        new StageAction(_player.IsPlaying ? "PAUSE" : "PLAY", TogglePlay),
        new StageAction("BACK 30s", () => Skip(-30_000)),
        new StageAction("FORWARD 30s", () => Skip(30_000)),
        new StageAction("RESTART", () => _player.Time = 0),

        // Audio/video sync, nudged while watching. The measured link delay (ADR-0059) seeds it and
        // is the wrong final answer on its own: the total also carries the file's container offset
        // and the decoder's buffering. So it is tuned by eye, which is what every media player
        // does. The captions say which way each one moves things, because a signed number on a
        // button tells nobody anything.
        new StageAction("SOUND EARLIER", () => Nudge(-1)),
        new StageAction($"SYNC: {VideoSync.Describe(_syncOffset)}", ResetSync),
        new StageAction("SOUND LATER", () => Nudge(1)),
    ];

    /// <summary>Jump by a number of milliseconds, clamped to the media.</summary>
    /// <remarks>
    /// Clamped rather than allowed to run past the end: VLC treats a seek beyond the length as
    /// a stop, so an over-shoot near the end would look like the file had ended early.
    /// </remarks>
    private void Skip(long milliseconds)
    {
        if (_player.Length <= 0)
        {
            return;
        }

        _player.Time = Math.Clamp(_player.Time + milliseconds, 0, _player.Length - 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_view is not null)
        {
            _view.Loaded -= OnLoaded;
        }

        _player.Stop();
        _player.Dispose();
        _libVlc.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        using var media = new Media(_libVlc, new Uri(_mediaPath));
        _player.Play(media);
        ApplySync();
    }

    /// <summary>Move the sync offset by one step and remember it.</summary>
    private void Nudge(int steps)
    {
        _syncOffset = VideoSync.Nudge(_syncOffset, steps);
        ApplySync();
        _remember?.Invoke(_syncOffset);
    }

    /// <summary>Back to what the measured delay suggested — a way out of an over-nudged offset.</summary>
    private void ResetSync()
    {
        _syncOffset = TimeSpan.Zero;
        ApplySync();
        _remember?.Invoke(_syncOffset);
    }

    /// <summary>
    /// Push the offset into the player.
    /// </summary>
    /// <remarks>
    /// <b>Called after every <c>Play</c>, not once at startup.</b> LibVLCSharp's own documentation
    /// says the audio delay <i>resets to zero each time the media changes</i> — so setting it once
    /// is a bug that works perfectly on the first file and silently stops working on the second.
    /// </remarks>
    private void ApplySync()
    {
        if (_disposed)
        {
            return;
        }

        _player.SetAudioDelay(VideoSync.ToMicroseconds(_syncOffset));
    }

    private void TogglePlay()
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }
    }

}

