using System.Diagnostics;
using DashDeck.Abstractions;
using DashDeck.Core.Arbitration;
using DashDeck.Core.Bus;
using DashDeck.Core.Catalog;
using DashDeck.Vehicle;

namespace DashDeck.Core;

/// <summary>
/// Runs the polling plan against the adapter and publishes decoded readings to the bus.
/// </summary>
/// <remarks>
/// The single place where the layers below (adapter, catalog, arbiter) meet the layer
/// above (the state bus). Everything here happens on one worker: the adapter is a
/// serialised resource, so there is exactly one loop asking it for things.
/// </remarks>
public sealed class VehicleService : IAsyncDisposable
{
    private readonly IVehicleAdapter _adapter;
    private readonly SignalCatalog _catalog;
    private readonly IClock _clock;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPolled = new(StringComparer.Ordinal);

    private Task? _loop;

    public VehicleService(IVehicleAdapter adapter, SignalCatalog catalog, IClock? clock = null)
    {
        _adapter = adapter;
        _catalog = catalog;
        _clock = clock ?? SystemClock.Instance;

        Arbiter = new RequestArbiter(catalog);
        Bus = new VehicleStateBus(catalog, Arbiter, _clock);
    }

    public RequestArbiter Arbiter { get; }

    public VehicleStateBus Bus { get; }

    /// <summary>Signals the vehicle answered at least once. Populated as polling proceeds.</summary>
    public IReadOnlySet<string> SupportedSignals => _supported;

    private readonly HashSet<string> _supported = new(StringComparer.Ordinal);

    /// <summary>Signals the vehicle explicitly refused, so the plan can stop asking.</summary>
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);

    /// <summary>Consecutive NO DATA replies per signal.</summary>
    private readonly Dictionary<string, int> _consecutiveNoData = new(StringComparer.Ordinal);

    /// <summary>
    /// How many consecutive NO DATA replies before a signal is written off as unsupported.
    /// One is far too few: adapters drop the occasional response, and a single unlucky poll
    /// would otherwise permanently kill a signal the vehicle supports perfectly well.
    /// </summary>
    public int NoDataToleranceCount { get; init; } = 3;

    public async Task StartAsync(CancellationToken ct)
    {
        await _adapter.InitializeAsync(ct).ConfigureAwait(false);

        // The adapter's measured ceiling becomes the arbiter's budget. Nothing assumes a
        // number: whatever the hardware reports is what components get to share.
        Arbiter.BudgetHz = _adapter.Capabilities?.MaxRequestsPerSecond ?? 1.0;

        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var entry = NextDue(out var waitFor);

            if (entry is null)
            {
                try
                {
                    await Task.Delay(waitFor, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await PollAsync(entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Shortest interval worth sleeping for. Below this, timer granularity costs more than
    /// the gap being waited out.
    /// </summary>
    private const double MinimumSleepSeconds = 0.020;

    /// <summary>
    /// Pick the signal that is most overdue <em>relative to its own interval</em>.
    /// </summary>
    /// <remarks>
    /// Scoring by absolute lateness looks reasonable and is wrong: under saturation a
    /// 0.1 Hz signal one second late would outrank a 4 Hz signal one second late, even
    /// though the first is 10% late and the second is 400% late. Slow signals then starve
    /// fast ones, which shows up as speed and RPM freezing while ambient temperature stays
    /// perfectly current. Relative lateness is the metric that matches intent.
    /// </remarks>
    private PlanEntry? NextDue(out TimeSpan waitFor)
    {
        waitFor = TimeSpan.FromMilliseconds(50);

        var plan = Arbiter.CurrentPlan;
        var now = _clock.UtcNow;

        PlanEntry? best = null;
        PlanEntry? soonestEntry = null;
        var bestScore = double.NegativeInfinity;
        var soonest = double.PositiveInfinity;

        foreach (var entry in plan.Entries)
        {
            if (entry.RateHz <= 0 || _unsupported.Contains(entry.Signal.Id))
            {
                continue;
            }

            var interval = entry.IntervalSeconds;
            var last = _lastPolled.GetValueOrDefault(entry.Signal.Id, DateTimeOffset.MinValue);

            if (last == DateTimeOffset.MinValue)
            {
                // Never polled. Take it now — the first reading of a signal is worth more
                // than keeping an already-flowing one perfectly on schedule.
                waitFor = TimeSpan.Zero;
                return entry;
            }

            var elapsed = (now - last).TotalSeconds;
            var score = elapsed / interval;

            if (score >= 1.0)
            {
                if (score > bestScore)
                {
                    bestScore = score;
                    best = entry;
                }
            }
            else if (interval - elapsed < soonest)
            {
                soonest = interval - elapsed;
                soonestEntry = entry;
            }
        }

        if (best is null && double.IsFinite(soonest))
        {
            if (soonest <= MinimumSleepSeconds)
            {
                // The next signal is due sooner than the OS timer can reliably sleep for.
                // Sleeping anyway overshoots by more than the gap itself and throws away a
                // large share of the request budget. Issuing the request a few milliseconds
                // early costs nothing — the adapter is the bottleneck regardless.
                waitFor = TimeSpan.Zero;
                return soonestEntry;
            }

            waitFor = TimeSpan.FromSeconds(Math.Clamp(soonest, MinimumSleepSeconds, 1.0));
        }

        return best;
    }

    /// <summary>
    /// The sustainable request ceiling, derived from how long requests actually take.
    /// This is the measurement open question Q12 asks for.
    /// </summary>
    public double MeasuredRequestsPerSecond =>
        _meanServiceSeconds > 0 ? 1.0 / _meanServiceSeconds : 0;

    private double _meanServiceSeconds;

    /// <summary>
    /// Update the ceiling from one request's round-trip time.
    /// </summary>
    /// <remarks>
    /// Measuring <em>achieved requests per second</em> instead is the obvious approach and
    /// is wrong: it cannot distinguish "the adapter could not go faster" from "nothing
    /// needed polling". Idle time then reads as incapacity, the budget shrinks, intervals
    /// lengthen, and that produces more idle time — a death spiral that ends with a fast
    /// adapter planned as though it were a slow one.
    /// <para>
    /// Service time has no such ambiguity. How long a request takes is a property of the
    /// hardware, not of how much work happens to be queued.
    /// </para>
    /// </remarks>
    private void RecordServiceTime(TimeSpan duration)
    {
        var seconds = Math.Max(duration.TotalSeconds, 0.0005);

        _meanServiceSeconds = _meanServiceSeconds <= 0
            ? seconds
            : (_meanServiceSeconds * 0.85) + (seconds * 0.15);

        // Plan against measured capability, never above what the adapter claims.
        var claimed = _adapter.Capabilities?.MaxRequestsPerSecond ?? MeasuredRequestsPerSecond;
        var budget = Math.Clamp(MeasuredRequestsPerSecond, 0.1, claimed);

        if (Math.Abs(budget - Arbiter.BudgetHz) > claimed * 0.05)
        {
            Arbiter.BudgetHz = budget;
        }
    }

    private async Task PollAsync(PlanEntry entry, CancellationToken ct)
    {
        var definition = entry.Signal;
        var spec = definition.ToRequest();
        var request = new PidRequest(spec.Mode, spec.Pid, spec.Bus);

        _lastPolled[definition.Id] = _clock.UtcNow;

        PidResponse response;
        var started = Stopwatch.GetTimestamp();
        try
        {
            response = await _adapter.RequestAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            RecordServiceTime(Stopwatch.GetElapsedTime(started));
        }

        if (!response.IsSuccess)
        {
            if (response.Failure == PidFailure.NoData)
            {
                // The vehicle does not support this PID. Stop asking: on a budget this
                // tight, repeatedly polling a signal that will never answer is spending
                // real capacity on nothing.
                _unsupported.Add(definition.Id);
                Bus.Publish(SignalValue.Missing(definition.Id, definition.Decode.Unit));
            }

            return;
        }

        var decoded = definition.Decode.Decode(response.Data);
        if (decoded is null || !definition.InRange(decoded.Value))
        {
            // Out-of-range means the decode spec is wrong or the response was corrupt.
            // Publishing it would put a plausible-looking wrong number on the dash.
            return;
        }

        _supported.Add(definition.Id);

        Bus.Publish(new SignalValue(
            definition.Id,
            decoded.Value,
            definition.Decode.Unit,
            response.TimestampUtc,
            Quality));
    }

    /// <summary>
    /// Quality stamped on published readings. Simulated sources say so, so mock data can
    /// never be mistaken for a truck on screen (ADR-0005).
    /// </summary>
    public SignalQuality Quality { get; set; } = SignalQuality.Live;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stopping.Dispose();
        await _adapter.DisposeAsync().ConfigureAwait(false);
    }
}
