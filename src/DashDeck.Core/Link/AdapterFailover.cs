using DashDeck.Abstractions;
using DashDeck.Vehicle;

namespace DashDeck.Core.Link;

/// <summary>
/// Watches for the real adapter while the dash runs on the simulator, and moves to it the
/// moment it answers (ADR-0034).
/// </summary>
/// <remarks>
/// The tablet is carried in and out of the truck, so the common start is in the house with the
/// adapter in the truck: the dash comes up simulated, with the SIM badge and the reason, as
/// ADR-0031 decided. That used to be where it stayed until a restart. Now this keeps asking the
/// chosen port — and, with relocation, the others — and on an answer it:
/// <list type="number">
/// <item>switches the bottom of the pipeline to the adapter (<see cref="SwitchableTransport"/>),
/// which makes <c>ElmAdapter</c> configure it before the next request;</item>
/// <item>stamps every reading from then on <see cref="SignalQuality.Live"/>;</item>
/// <item>has <see cref="VehicleService"/> forget what the simulator taught it.</item>
/// </list>
/// One way only. Once live, a pulled cable is <c>ADAPTER LOST</c> and Stale values, never a
/// quiet return to made-up numbers.
/// </remarks>
public sealed class AdapterFailover : IAsyncDisposable
{
    private readonly VehicleService _service;
    private readonly SwitchableTransport _switchable;
    private readonly Func<CancellationToken, Task<IVehicleTransport?>> _tryGoLive;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _watching;
    private Task? _loop;

    /// <param name="service">The running pipeline.</param>
    /// <param name="switchable">The bottom of it, currently on the simulator.</param>
    /// <param name="tryGoLive">One attempt to reach the adapter: a connected transport, or null.</param>
    /// <param name="interval">The pause between attempts.</param>
    public AdapterFailover(
        VehicleService service,
        SwitchableTransport switchable,
        Func<CancellationToken, Task<IVehicleTransport?>> tryGoLive,
        TimeSpan? interval = null)
    {
        _service = service;
        _switchable = switchable;
        _tryGoLive = tryGoLive;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>True once the switch to the real adapter has happened.</summary>
    public bool IsLive { get; private set; }

    /// <summary>True while it is looking.</summary>
    public bool IsWatching => _loop is { IsCompleted: false };

    /// <summary>Raised once, on the watcher's thread, when the dash goes live.</summary>
    public event Action? WentLive;

    /// <summary>Start looking. Does nothing if already looking or already live.</summary>
    public void Start()
    {
        if (IsLive || IsWatching)
        {
            return;
        }

        _watching = new CancellationTokenSource();
        var ct = _watching.Token;
        _loop = Task.Run(() => WatchAsync(ct), CancellationToken.None);
    }

    /// <summary>Stop looking — when the user chooses the simulator, say.</summary>
    public async Task StopAsync()
    {
        if (_watching is null)
        {
            return;
        }

        await _watching.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _watching.Dispose();
        _watching = null;
        _loop = null;
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IVehicleTransport? live = null;

            try
            {
                live = await _tryGoLive(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Not there yet.
            }

            if (live is not null)
            {
                await GoLiveAsync(live, ct).ConfigureAwait(false);
                return;
            }

            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }

    private async Task GoLiveAsync(IVehicleTransport live, CancellationToken ct)
    {
        // Switch first, then Live. A poll takes its quality before it asks (VehicleService), so
        // this order means a simulated reply can never be stamped Live; at worst one real reply
        // is labelled Simulated, which errs the safe way.
        await _switchable.SwitchToAsync(live, ct).ConfigureAwait(false);
        _service.Quality = SignalQuality.Live;
        _service.ResetLearning();
        IsLive = true;
        WentLive?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
