using System.Windows;
using System.Windows.Media;
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

    private VideoView? _view;
    private bool _disposed;

    public VideoStageOccupant(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        // Locates and loads the native libvlc, once per process. Fully qualified because
        // DashDeck.Core is a namespace of ours and wins the short name.
        LibVLCSharp.Shared.Core.Initialize();

        _libVlc = new LibVLC();
        _player = new VlcPlayer(_libVlc);
        _mediaPath = mediaPath;
    }

    /// <inheritdoc />
    public string Name => "VIDEO";

    /// <inheritdoc />
    public int PreferredBands => 3;

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
    }
}
