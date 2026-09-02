using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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

    // The action bar's controls, kept so the ticker can walk them along with the player.
    private Button? _playPause;
    private Slider? _position;
    private TextBlock? _elapsed;
    private DispatcherTimer? _ticker;
    private bool _syncingSlider;

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
    /// Transport controls, on the stage's action bar.
    /// </summary>
    /// <remarks>
    /// The reason this could not exist before. The picture lives in a child window that draws
    /// over all WPF content whatever the z-order says, so controls overlaid on the video were
    /// invisible and untappable — which is F8, and why the stage grew a real row for them
    /// rather than a floating panel.
    /// <para>
    /// The bar drives the player and the player drives the bar: a timer walks the slider
    /// while it plays, and moving the slider seeks. The flag is what stops those two fighting
    /// each other every tick.
    /// </para>
    /// </remarks>
    public FrameworkElement? CreateActionBar()
    {
        _playPause = ActionBar.Button("PAUSE", TogglePlay, 140);

        _position = new Slider
        {
            Width = 430,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Minimum = 0,
            Maximum = 1,
            IsMoveToPointEnabled = true,
        };

        _position.ValueChanged += (_, e) =>
        {
            if (!_syncingSlider && Math.Abs(e.NewValue - _player.Position) > 0.001)
            {
                _player.Position = (float)e.NewValue;
            }
        };

        _elapsed = ActionBar.Caption("--:-- / --:--");

        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };

        _ticker.Tick += (_, _) => SyncBar();
        _ticker.Start();

        return ActionBar.Row(_playPause, _position, _elapsed);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticker?.Stop();

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

        SyncBar();
    }

    /// <summary>Walk the bar to wherever the player actually is.</summary>
    private void SyncBar()
    {
        if (_disposed)
        {
            return;
        }

        if (_playPause?.Content is TextBlock caption)
        {
            caption.Text = _player.IsPlaying ? "PAUSE" : "PLAY";
        }

        if (_position is not null)
        {
            // The flag is load-bearing: writing Value raises ValueChanged, which would seek
            // the player to where it already is, twice a second, forever.
            _syncingSlider = true;
            _position.Value = double.IsNaN(_player.Position) ? 0 : Math.Clamp(_player.Position, 0, 1);
            _syncingSlider = false;
        }

        if (_elapsed is not null)
        {
            var length = _player.Length;

            _elapsed.Text = length > 0
                ? $"{Clock(_player.Time)} / {Clock(length)}"
                : "--:-- / --:--";
        }
    }

    /// <summary>Milliseconds as <c>m:ss</c>, or <c>h:mm:ss</c> once it earns the hours.</summary>
    private static string Clock(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(milliseconds, 0));

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
