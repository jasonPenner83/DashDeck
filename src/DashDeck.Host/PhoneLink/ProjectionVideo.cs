using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

// WPF has a MediaPlayer of its own; this is LibVLC's.
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// The phone's screen, decoded: H.264 from the dongle in, a <see cref="WriteableBitmap"/> on the
/// stage out (ADR-0057).
/// </summary>
/// <remarks>
/// LibVLC decodes — it already ships for VIDEO, so no new dependency — reading raw Annex-B H.264 from
/// an <see cref="H264Pipe"/> with no caching, and hands each picture to memory callbacks rather than
/// to a window of its own. <b>The picture stays in WPF's visual tree</b>, so the stage clips it, the
/// three-dot menu draws over it and touch lands on WPF, not on a native child window that would
/// swallow it (the trap a hosted program taught, ADR-0021). The bitmap is written on the dispatcher;
/// a frame that arrives while the last is still waiting to be drawn replaces it rather than queueing.
/// </remarks>
public sealed class ProjectionVideo : IDisposable
{
    private const int BytesPerPixel = 4;

    private readonly Dispatcher _dispatcher;
    private readonly H264Pipe _pipe = new();
    private readonly object _gate = new();

    // Held so the garbage collector cannot take the delegates LibVLC calls back on.
    private readonly VlcPlayer.LibVLCVideoFormatCb _format;
    private readonly VlcPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly VlcPlayer.LibVLCVideoLockCb _lock;
    private readonly VlcPlayer.LibVLCVideoDisplayCb _display;

    private LibVLC? _libVlc;
    private VlcPlayer? _player;
    private Media? _media;
    private StreamMediaInput? _input;

    private IntPtr _frame;
    private int _width;
    private int _height;
    private bool _pending;
    private bool _disposed;

    public ProjectionVideo(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _format = OnFormat;
        _cleanup = OnCleanup;
        _lock = OnLock;
        _display = OnDisplay;
    }

    /// <summary>The picture, once a frame has been decoded. Replaced when the phone changes size.</summary>
    public WriteableBitmap? Picture { get; private set; }

    /// <summary>Pictures drawn.</summary>
    public long FramesShown { get; private set; }

    /// <summary>Why it cannot decode, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised on the dispatcher when <see cref="Picture"/> is a new bitmap.</summary>
    public event Action<WriteableBitmap>? PictureChanged;

    /// <summary>Start the decoder. False, with <see cref="Problem"/> set, if LibVLC is not there.</summary>
    public bool Start()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();

            // Live, not a file: no buffering, no clock to keep, no window of its own.
            _libVlc = new LibVLC("--no-audio", "--no-osd", "--no-video-title-show", "--quiet");
            _player = new VlcPlayer(_libVlc);
            _player.SetVideoFormatCallbacks(_format, _cleanup);
            _player.SetVideoCallbacks(_lock, null, _display);

            _input = new StreamMediaInput(_pipe);
            _media = new Media(
                _libVlc,
                _input,
                ":demux=h264",
                ":h264-fps=60",
                ":file-caching=0",
                ":network-caching=0",
                ":live-caching=0",
                ":clock-jitter=0",
                ":clock-synchro=0");

            return _player.Play(_media);
        }
        catch (Exception ex) when (ex is DllNotFoundException or VLCException or TypeInitializationException or InvalidOperationException)
        {
            Problem = "The video decoder (LibVLC) could not start: " + ex.Message;
            return false;
        }
    }

    /// <summary>Feed H.264 as it arrived from the dongle.</summary>
    public void Push(ReadOnlyMemory<byte> h264) => _pipe.Push(h264.Span);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // End the stream first, so LibVLC's read returns and Stop does not wait on it.
        _pipe.Complete();
        _player?.Stop();
        _player?.Dispose();
        _media?.Dispose();
        _input?.Dispose();
        _libVlc?.Dispose();
        _pipe.Dispose();

        lock (_gate)
        {
            FreeFrame();
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Ask for 32-bit BGRA, the WriteableBitmap's own layout, at the size the phone sent.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        pitches = width * BytesPerPixel;
        lines = height;

        lock (_gate)
        {
            FreeFrame();
            _width = (int)width;
            _height = (int)height;
            _frame = Marshal.AllocHGlobal(_width * _height * BytesPerPixel);
        }

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _frame);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_gate)
        {
            if (_pending || _disposed)
            {
                return;
            }

            _pending = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, Draw);
    }

    private void Draw()
    {
        lock (_gate)
        {
            _pending = false;

            if (_disposed || _frame == IntPtr.Zero)
            {
                return;
            }

            var created = false;
            if (Picture is null || Picture.PixelWidth != _width || Picture.PixelHeight != _height)
            {
                Picture = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgr32, null);
                created = true;
            }

            Picture.WritePixels(
                new Int32Rect(0, 0, _width, _height),
                _frame,
                _width * _height * BytesPerPixel,
                _width * BytesPerPixel);

            FramesShown++;

            if (created)
            {
                PictureChanged?.Invoke(Picture);
            }
        }
    }

    private void FreeFrame()
    {
        if (_frame != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frame);
            _frame = IntPtr.Zero;
        }
    }
}
