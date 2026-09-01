using DashDeck.Abstractions;
using DashDeck.Core.Arbitration;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Bus;

/// <summary>
/// The in-memory snapshot of every signal, plus pub/sub over new readings.
/// </summary>
/// <remarks>
/// Implements <see cref="IVehicleSignals"/>, which is the entire surface a component sees
/// of the vehicle. Readings age out to <see cref="SignalQuality.Stale"/> rather than
/// disappearing, because a dash that blanks a value the instant a poll is late is worse
/// than one that shows the last known number and says how old it is.
/// </remarks>
public sealed class VehicleStateBus : IVehicleSignals
{
    private readonly SignalCatalog _catalog;
    private readonly RequestArbiter _arbiter;
    private readonly IClock _clock;
    private readonly Dictionary<string, SignalValue> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<SignalValue>>> _observers = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public VehicleStateBus(SignalCatalog catalog, RequestArbiter arbiter, IClock? clock = null)
    {
        _catalog = catalog;
        _arbiter = arbiter;
        _clock = clock ?? SystemClock.Instance;
    }

    public IReadOnlyCollection<string> KnownSignals => _catalog.Ids;

    public SignalValue Current(string signalId)
    {
        lock (_sync)
        {
            if (!_latest.TryGetValue(signalId, out var value))
            {
                var unit = _catalog.TryGet(signalId, out var def) ? def.Decode.Unit : string.Empty;
                return SignalValue.Missing(signalId, unit);
            }

            if (!_catalog.TryGet(signalId, out var definition))
            {
                return value;
            }

            return _clock.UtcNow - value.TimestampUtc > StalenessBudgetFor(definition)
                ? value.AsStale()
                : value;
        }
    }

    /// <summary>
    /// How old a reading may be before it counts as stale, judged against the rate the
    /// arbiter actually allocated.
    /// </summary>
    /// <remarks>
    /// Judging staleness against the signal's <em>requested</em> rate is wrong: a signal
    /// the arbiter deliberately degraded to 1 Hz would be marked stale for failing to
    /// arrive at 4 Hz, so the arbiter's own decisions would make good data look broken.
    /// Degradation should slow a value down, not blank it.
    /// </remarks>
    private TimeSpan StalenessBudgetFor(SignalDefinition definition)
    {
        var allocated = _arbiter.CurrentPlan.Entries
            .FirstOrDefault(e => string.Equals(e.Signal.Id, definition.Id, StringComparison.Ordinal))
            ?.RateHz ?? 0;

        if (allocated <= 0)
        {
            return definition.StalenessBudget;
        }

        var effective = Math.Min(allocated, definition.DefaultRateHz);
        return TimeSpan.FromSeconds(Math.Max(2.0, 5.0 / Math.Max(effective, 0.02)));
    }

    public IDisposable Subscribe(string signalId, Action<SignalValue> onValue)
    {
        lock (_sync)
        {
            if (!_observers.TryGetValue(signalId, out var list))
            {
                list = [];
                _observers[signalId] = list;
            }

            list.Add(onValue);
        }

        return new Unsubscriber(this, signalId, onValue);
    }

    public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz) =>
        _arbiter.Declare(signalId, priority, rateHz);

    /// <summary>
    /// Publish a decoded reading. Called by the polling loop; components never call this.
    /// </summary>
    public void Publish(SignalValue value)
    {
        Action<SignalValue>[] observers;

        lock (_sync)
        {
            _latest[value.SignalId] = value;
            observers = _observers.TryGetValue(value.SignalId, out var list)
                ? [.. list]
                : [];
        }

        // Dispatched outside the lock: an observer that blocks must not stall the bus, and
        // one that throws must not take the polling loop down with it.
        foreach (var observer in observers)
        {
            try
            {
                observer(value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ObserverFaulted?.Invoke(value.SignalId, ex);
            }
        }
    }

    /// <summary>
    /// Raised when a subscriber throws. The host uses this to fault the offending
    /// component rather than letting one bad observer degrade the whole dash.
    /// </summary>
    public event Action<string, Exception>? ObserverFaulted;

    /// <summary>A point-in-time copy of every known reading, for debug views and tests.</summary>
    public IReadOnlyDictionary<string, SignalValue> Snapshot()
    {
        lock (_sync)
        {
            return _latest.Keys.ToDictionary(id => id, Current, StringComparer.Ordinal);
        }
    }

    private void Unsubscribe(string signalId, Action<SignalValue> observer)
    {
        lock (_sync)
        {
            if (_observers.TryGetValue(signalId, out var list))
            {
                list.Remove(observer);
            }
        }
    }

    private sealed class Unsubscriber(VehicleStateBus bus, string signalId, Action<SignalValue> observer)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bus.Unsubscribe(signalId, observer);
        }
    }
}
