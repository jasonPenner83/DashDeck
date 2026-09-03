using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Host.PhoneLink;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Android Auto and CarPlay on the stage, by way of a dongle.
/// </summary>
/// <remarks>
/// <b>There is no hardware yet</b>, and this screen says so rather than looking broken. A
/// Carlinkit CPC200 is chosen and not ordered (ADR-0019); until one exists the synthetic
/// transport answers the handshake and sends no picture, which is the honest state and the
/// one worth rendering.
/// <para>
/// Everything above the transport is finished: framing, the session, the heartbeat and the
/// touch mapping all run here exactly as they will against the real device.
/// </para>
/// </remarks>
public sealed partial class PhoneLinkViewModel : ObservableObject, IDisposable
{
    private readonly DongleClient _client;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stopping = new();

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

    public PhoneLinkViewModel(IClock clock, IDongleTransport? transport = null)
    {
        // Synthetic by default, and named as synthetic on screen. Swapping this for
        // UsbDongleTransport is the whole of the work when the dongle arrives.
        _client = new DongleClient(transport ?? new SyntheticDongleTransport(clock), clock);
        _client.StateChanged += _ => Refresh();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _ = Start();
    }

    /// <summary>How the surface is sized when it asks the phone to project.</summary>
    /// <remarks>
    /// The stage's own content area, not the whole screen: the phone renders to exactly this,
    /// so anything else produces a correctly decoded picture of the wrong shape.
    /// </remarks>
    public const int ProjectionWidth = 912;

    public const int ProjectionHeight = 513;

    /// <summary>Frames per second asked of the phone.</summary>
    public const int ProjectionFrameRate = 30;

    /// <summary>One line describing the state, for <c>--shot</c>.</summary>
    public string Describe() =>
        $"state={_client.State} transport={_client.TransportName} " +
        $"frames={_client.VideoFrames} unknown={_client.UnknownMessages}";

    /// <summary>Send a touch through to the phone, in fractions of the projected surface.</summary>
    public Task TouchAsync(TouchAction action, double fractionX, double fractionY) =>
        _client.TouchAsync(action, fractionX, fractionY);

    /// <summary>Ask the dongle to hand the session back to the phone.</summary>
    [RelayCommand]
    private void Reconnect() => _ = Start();

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

        // Blocking here is acceptable: it is a stage change, and an orphaned pump holding the
        // device is how the next attempt finds it busy.
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stopping.Dispose();
    }

    private async Task Start()
    {
        await _client.StartAsync(
            ProjectionWidth,
            ProjectionHeight,
            ProjectionFrameRate,
            _stopping.Token).ConfigureAwait(true);

        Refresh();
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        TransportText = _client.TransportName;
        FramesText = $"{_client.VideoFrames} frames";
        IsProjecting = _client.State is PhoneLinkState.Projecting && _client.VideoFrames > 0;

        (Headline, Detail) = _client.State switch
        {
            PhoneLinkState.NoDongle => (
                "NO DONGLE",
                "Plug in a Carlinkit CPC200. Android Auto needs one — Google licenses no receiver for a PC."),

            PhoneLinkState.WaitingForPhone => (
                "WAITING FOR A PHONE",
                "The dongle is listening. Connect a phone by USB or let it pair over Wi-Fi."),

            // Connected, and still no picture — which is the true state until the USB
            // transport exists. Saying "projecting" over a black rectangle would be the one
            // dishonest screen on this dash.
            PhoneLinkState.Projecting when _client.VideoFrames == 0 => (
                "LINKED, NO PICTURE",
                "The session is open and no video has arrived. Expected against the synthetic dongle: it answers the handshake and sends no frames."),

            PhoneLinkState.Projecting => ("PROJECTING", string.Empty),

            _ => ("LINK LOST", "The dongle stopped answering. Reconnect to try again."),
        };
    }
}
