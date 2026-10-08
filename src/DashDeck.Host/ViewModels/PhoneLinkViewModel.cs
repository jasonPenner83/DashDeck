using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Host.PhoneLink;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Android Auto and CarPlay on the stage, by way of a Carlinkit dongle (ADR-0019, ADR-0057).
/// </summary>
/// <remarks>
/// The dongle is reached over USB (<see cref="UsbDongleTransport"/>); its H.264 is decoded into the
/// stage (<see cref="ProjectionVideo"/>) and its PCM played on Windows' default output
/// (<see cref="ProjectionAudio"/>). With no dongle plugged in the screen says why — not plugged in, or
/// not bound to WinUSB — and looks again every few seconds, so plugging it in is enough.
/// <para>
/// <c>--synthetic-dongle</c> runs the screen against the synthetic dongle at a desk: it answers the
/// handshake, announces an Android Auto phone, and sends no picture, and says so.
/// </para>
/// </remarks>
public sealed partial class PhoneLinkViewModel : ObservableObject, IDisposable
{
    /// <summary>How often a missing dongle is looked for again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly IClock _clock;
    private readonly Func<IDongleTransport> _makeTransport;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly ProjectionAudio _audio = new();
    private readonly CancellationTokenSource _stopping = new();

    private DongleClient _client;
    private IDongleTransport _transport;
    private readonly ProjectionVideo _video;
    private bool _videoStarted;
    private DateTimeOffset _lastAttempt;
    private bool _starting;
    private bool _disposed;

    [ObservableProperty]
    private string _headline = "CONNECTING";

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _transportText = string.Empty;

    [ObservableProperty]
    private string _framesText = "0 frames";

    [ObservableProperty]
    private bool _isProjecting;

    [ObservableProperty]
    private ImageSource? _picture;

    public PhoneLinkViewModel(IClock clock, IDongleTransport? transport = null)
    {
        _clock = clock;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _video = new ProjectionVideo(_dispatcher);
        _video.PictureChanged += bitmap => Picture = bitmap;
        _makeTransport = transport is not null
            ? () => transport
            : UseSyntheticDongle
                ? () => new SyntheticDongleTransport(clock)
                : () => new UsbDongleTransport();

        (_transport, _client) = MakeClient();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        _ = Start();
    }

    /// <summary>Set from <c>--synthetic-dongle</c>: the phone screen talks to a synthetic dongle.</summary>
    public static bool UseSyntheticDongle { get; set; }

    /// <summary>
    /// The size the phone is asked to render to: the stage's own (912 × 636, ADR-0018).
    /// </summary>
    /// <remarks>
    /// Whatever size actually arrives is drawn uniformly scaled and centred, and touch is mapped to
    /// the picture as drawn — so a phone that renders its own size still lines up under a finger.
    /// </remarks>
    public const int ProjectionWidth = 912;

    public const int ProjectionHeight = 636;

    /// <summary>Frames per second asked of the phone.</summary>
    public const int ProjectionFrameRate = 30;

    /// <summary>One line describing the state, for <c>--shot</c>.</summary>
    public string Describe() =>
        $"state={_client.State} transport={_client.TransportName} phone={_client.PhoneType} " +
        $"frames={_client.VideoFrames} shown={_video.FramesShown} audio={_client.AudioPackets} " +
        $"unknown={_client.UnknownMessages}";

    /// <summary>Send a touch through to the phone, in fractions of the projected picture.</summary>
    public Task TouchAsync(TouchAction action, double fractionX, double fractionY) =>
        _client.TouchAsync(action, fractionX, fractionY);

    /// <summary>Close the dongle and open it again.</summary>
    [RelayCommand]
    private void Reconnect() => _ = Restart();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _stopping.Cancel();

        // Off the dispatcher, with a limit: the client's awaits must not come back to a thread that
        // is blocked waiting on them (the trap that kept the serial port, CLAUDE.md).
        var client = _client;
        Task.Run(() => client.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));

        _video.Dispose();
        _audio.Dispose();
        _stopping.Dispose();
    }

    private (IDongleTransport Transport, DongleClient Client) MakeClient()
    {
        var transport = _makeTransport();
        var client = new DongleClient(transport, _clock);
        client.StateChanged += _ => _dispatcher.BeginInvoke(Refresh);
        client.VideoArrived += OnVideo;
        client.AudioArrived += _audio.Play;
        return (transport, client);
    }

    private async Task Start()
    {
        if (_starting || _disposed)
        {
            return;
        }

        _starting = true;
        _lastAttempt = _clock.UtcNow;

        try
        {
            await _client.StartAsync(
                new DongleSetup(ProjectionWidth, ProjectionHeight, ProjectionFrameRate),
                _stopping.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            _starting = false;
        }

        Refresh();
    }

    private async Task Restart()
    {
        if (_disposed || _starting)
        {
            return;
        }

        var old = _client;
        (_transport, _client) = MakeClient();
        await Task.Run(() => old.DisposeAsync().AsTask()).ConfigureAwait(true);
        await Start().ConfigureAwait(true);
    }

    private void Tick()
    {
        // A dongle not there, or one that dropped, is looked for again — plugging it in is enough.
        if (_client.State is PhoneLinkState.NoDongle or PhoneLinkState.Lost
            && _clock.UtcNow - _lastAttempt >= RetryInterval)
        {
            _ = Restart();
        }

        Refresh();
    }

    private void OnVideo(ReadOnlyMemory<byte> h264)
    {
        // Queued before the decoder exists, so the first frame — the one carrying the stream's
        // parameters — is not lost. The decoder starts on it, so LibVLC is not loaded for a dongle
        // with no phone.
        _video.Push(h264);

        if (!_videoStarted)
        {
            _videoStarted = true;
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    _video.Start();
                }
            });
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var shown = _video.FramesShown;
        TransportText = _client.TransportName;
        FramesText = $"{_client.VideoFrames} frames · {_client.AudioPackets} audio";
        IsProjecting = _client.State is PhoneLinkState.Projecting && shown > 0;

        var phone = _client.PhoneType switch
        {
            PhoneType.AndroidAuto => "ANDROID AUTO",
            PhoneType.CarPlay => "CARPLAY",
            PhoneType.AndroidMirror or PhoneType.IPhoneMirror => "SCREEN MIRROR",
            PhoneType.HiCar => "HICAR",
            _ => "PHONE",
        };

        (Headline, Detail) = _client.State switch
        {
            PhoneLinkState.NoDongle => (
                "NO DONGLE",
                (_transport.Problem ?? "Plug in the Carlinkit dongle.") + " Looking again every few seconds."),

            PhoneLinkState.WaitingForPhone => (
                "WAITING FOR A PHONE",
                "The dongle is listening. Pair the phone with it over Bluetooth once (it is called DashDeck); after that it connects by itself."),

            PhoneLinkState.Projecting when _video.Problem is { } problem => ($"{phone}, NO PICTURE", problem),

            // Connected and nothing decoded yet. Saying "projecting" over a black rectangle would be
            // the one dishonest screen on this dash.
            PhoneLinkState.Projecting when shown == 0 => (
                $"{phone} CONNECTED",
                _client.VideoFrames == 0
                    ? "The session is open and no picture has arrived yet."
                    : "Video is arriving and being decoded."),

            PhoneLinkState.Projecting => (phone, string.Empty),

            _ => ("LINK LOST", "The dongle stopped answering. Looking again every few seconds."),
        };

        if (_audio.Problem is { } audio && IsProjecting)
        {
            Detail = audio;
        }
    }
}
