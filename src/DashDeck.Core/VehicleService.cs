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
    /// <remarks>A snapshot: the set itself is written by the polling worker.</remarks>
    public IReadOnlySet<string> SupportedSignals
    {
        get
        {
            lock (_statusLock)
            {
                return _supported.ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    private readonly HashSet<string> _supported = new(StringComparer.Ordinal);

    /// <summary>Signals the vehicle explicitly refused, so the plan can stop asking.</summary>
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards the two sets above. The worker is still their only writer; the lock exists for
    /// the settings inventory, which reads them from the UI thread.
    /// </summary>
    private readonly Lock _statusLock = new();

    /// <summary>What polling has learned about one signal, for the settings inventory to show.</summary>
    public SignalPollStatus StatusOf(string signalId)
    {
        lock (_statusLock)
        {
            return _supported.Contains(signalId) ? SignalPollStatus.Answered
                : _unsupported.Contains(signalId) ? SignalPollStatus.Retired
                : _lastPolled.ContainsKey(signalId) ? SignalPollStatus.Asked
                : SignalPollStatus.NotAsked;
        }
    }

    /// <summary>
    /// Ask the adapter one question, outside the polling plan.
    /// </summary>
    /// <remarks>
    /// For discovery from Settings (ADR-0032): a supported-PID scan, or a TEST of a definition
    /// before it is saved. It goes through the same adapter as the plan, whose own gate
    /// serialises it between polls, so it can never interleave on the wire. It is a handful of
    /// requests a person asks for by hand — not polling, and nothing here repeats it — so it
    /// does not take a share of the arbiter's budget; the plan simply waits one exchange.
    /// </remarks>
    public Task<PidResponse> ProbeAsync(PidRequest request, CancellationToken ct) =>
        _adapter.RequestAsync(request, ct);

    /// <summary>
    /// Consecutive <c>NO DATA</c> answers per signal, cleared by any successful decode.
    /// Only touched from the polling worker, like <see cref="_unsupported"/>.
    /// </summary>
    private readonly Dictionary<string, int> _consecutiveNoData = new(StringComparer.Ordinal);

    /// <summary>
    /// How many consecutive refusals retire a signal that has never once answered.
    /// </summary>
    private const int NoDataRefusalsBeforeRetiring = 4;

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
            if (_resetRequested)
            {
                ApplyReset();
            }

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

            try
            {
                await PollAsync(entry, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // This loop is the only thing that ever asks the truck anything. If it ends, the
                // dash goes Stale for good and a reconnected cable is never noticed — which is
                // exactly what a stray exception from a pulled USB device did (ADR-0034). So it
                // never ends on an error: note it, pause a moment, and carry on.
                LastPollError = $"{ex.GetType().Name}: {ex.Message}";

                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The last unexpected error a poll hit, kept for diagnostics; the loop carries on regardless.</summary>
    public string? LastPollError { get; private set; }

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
            DateTimeOffset last;

            lock (_statusLock)
            {
                if (entry.RateHz <= 0 || _unsupported.Contains(entry.Signal.Id))
                {
                    continue;
                }

                last = _lastPolled.GetValueOrDefault(entry.Signal.Id, DateTimeOffset.MinValue);
            }

            var interval = entry.IntervalSeconds;

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

        if (!definition.HasRequest)
        {
            PublishPlaceholder(definition);
            return;
        }

        var spec = definition.ToRequest();
        var request = new PidRequest(spec.Mode, spec.Pid, spec.Bus, spec.Module);

        lock (_statusLock)
        {
            _lastPolled[definition.Id] = _clock.UtcNow;
        }

        // Taken before the request, not after the reply: during a switch from the simulator to
        // the truck (ADR-0034) a reply may come from either side of it, and only a quality
        // decided before asking guarantees a simulated reply is never stamped Live — the worst
        // case is the reverse, a real value labelled Simulated for one poll.
        var quality = Quality;

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

        // Only exchanges the adapter actually answered describe its throughput. A pulled
        // cable comes back as Timeout in microseconds — ElmAdapter turns the IOException
        // into one, deliberately, because a dropped cable is routine — and averaging those
        // in sends the measured ceiling to absurd heights at the exact moment nothing is
        // getting through. Q12 reads its answer off this number, so it may only ever
        // describe the adapter replying.
        if (response.IsSuccess || response.Failure is PidFailure.NoData or PidFailure.Rejected)
        {
            RecordServiceTime(Stopwatch.GetElapsedTime(started));
        }

        if (!response.IsSuccess)
        {
            // A negative response from an addressed module (ADR-0035) is the same news as NO DATA
            // from the broadcast — this is not something it will give — and earns the same
            // patience, because "conditions not correct" with the engine off is not forever.
            if (response.Failure is PidFailure.NoData or PidFailure.Rejected)
            {
                RecordNoData(definition);
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

        lock (_statusLock)
        {
            _supported.Add(definition.Id);
        }

        _consecutiveNoData.Remove(definition.Id);

        Bus.Publish(new SignalValue(
            definition.Id,
            decoded.Value,
            definition.Decode.Unit,
            response.TimestampUtc,
            quality));
    }

    /// <summary>
    /// A placeholder's turn (ADR-0052): nothing is asked — it has no request. While simulated, the
    /// synthetic truck's value of the same name is published, flagged Simulated; otherwise the
    /// signal reads Unavailable, which is the truth about an identifier nobody has found.
    /// </summary>
    private void PublishPlaceholder(SignalDefinition definition)
    {
        lock (_statusLock)
        {
            _lastPolled[definition.Id] = _clock.UtcNow;
        }

        var quality = Quality;
        if (quality == SignalQuality.Simulated && SimulatedValues?.Invoke(definition.Id) is { } value && double.IsFinite(value))
        {
            Bus.Publish(new SignalValue(definition.Id, value, definition.Decode.Unit, _clock.UtcNow, quality));
            return;
        }

        Bus.Publish(SignalValue.Missing(definition.Id, definition.Decode.Unit));
    }

    /// <summary>
    /// The synthetic truck's quantities by signal id, for placeholders while simulated (ADR-0052);
    /// null when there is no synthetic truck.
    /// </summary>
    public Func<string, double?>? SimulatedValues { get; set; }

    /// <summary>
    /// Decide whether a <c>NO DATA</c> answer means the vehicle has no such PID, or simply
    /// that this one did not come back.
    /// </summary>
    /// <remarks>
    /// The adapter says <c>NO DATA</c> for both, and getting the distinction wrong is
    /// expensive in one direction: retiring a working signal blanks it for the rest of the
    /// session. Worse, the odds scale with request count, so the *higher* a signal's rate
    /// the sooner it dies — speed and RPM at 4 Hz would go dark within seconds while a
    /// 0.2 Hz fuel level survived, which is exactly how this was found.
    /// <para>
    /// Two rules. A signal that has ever decoded a reading is supported, full stop; any
    /// later <c>NO DATA</c> is a dropped response. One that has never answered is retired
    /// only after several *consecutive* refusals, so a drop during the first few polls does
    /// not condemn it.
    /// </para>
    /// </remarks>
    private void RecordNoData(SignalDefinition definition)
    {
        lock (_statusLock)
        {
            if (_supported.Contains(definition.Id))
            {
                return;
            }
        }

        var refusals = _consecutiveNoData.GetValueOrDefault(definition.Id) + 1;
        _consecutiveNoData[definition.Id] = refusals;

        if (refusals < NoDataRefusalsBeforeRetiring)
        {
            return;
        }

        // Never answered, and asked repeatedly. On a budget this tight, polling a signal
        // that will never reply is spending real capacity on nothing.
        lock (_statusLock)
        {
            _unsupported.Add(definition.Id);
        }

        Bus.Publish(SignalValue.Missing(definition.Id, definition.Decode.Unit));
    }

    /// <summary>
    /// Quality stamped on published readings. Simulated sources say so, so mock data can
    /// never be mistaken for a truck on screen (ADR-0005).
    /// </summary>
    public SignalQuality Quality
    {
        get => _quality;
        set => _quality = value;
    }

    // Written by whoever switches the link (ADR-0034), read by the polling worker.
    private volatile SignalQuality _quality = SignalQuality.Live;

    /// <summary>
    /// Forget everything polling has learned: which signals answered, which were retired, the
    /// measured service time.
    /// </summary>
    /// <remarks>
    /// For a switch from the simulator to the truck (ADR-0034). What the synthetic ECU answered
    /// says nothing about the real one, and a signal the simulator never implemented must not
    /// stay retired on a truck that has it. Applied by the polling worker at the top of its next
    /// loop, because the dictionaries it clears are the worker's own.
    /// </remarks>
    public void ResetLearning() => _resetRequested = true;

    private volatile bool _resetRequested;

    private void ApplyReset()
    {
        _resetRequested = false;

        lock (_statusLock)
        {
            _supported.Clear();
            _unsupported.Clear();
            _lastPolled.Clear();
        }

        _consecutiveNoData.Clear();
        _meanServiceSeconds = 0;
        Arbiter.BudgetHz = _adapter.Capabilities?.MaxRequestsPerSecond ?? Arbiter.BudgetHz;
    }

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

/// <summary>What the polling loop knows about one signal.</summary>
public enum SignalPollStatus
{
    /// <summary>Nothing has asked for it since launch, so the truck has not been asked either.</summary>
    NotAsked,

    /// <summary>Asked, and no reading has decoded yet.</summary>
    Asked,

    /// <summary>The vehicle has answered it with an in-range value at least once.</summary>
    Answered,

    /// <summary>Refused repeatedly without ever answering, and no longer polled.</summary>
    Retired,
}
