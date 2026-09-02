using System.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace DashDeck.Host.Tests;

/// <summary>
/// The contract every widget leans on: a value is either trustworthy and rendered, or it is
/// visibly absent. Never a stale digit passed off as current, and never a zero standing in
/// for "no idea".
/// </summary>
public sealed class ObservableSignalTests
{
    [Fact]
    public void AbsentValueRendersAsThePlaceholderRatherThanAZero()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        using var signal = new ObservableSignal(signals, "vehicle.speed");

        Assert.Equal(SignalQuality.Unavailable, signal.Quality);
        Assert.False(signal.IsUsable);
        Assert.Equal(ObservableSignal.Placeholder, signal.Text);
    }

    [Fact]
    public void LiveValueRendersFormatted()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        using var signal = new ObservableSignal(signals, "vehicle.speed", format: "0");

        signals.Push(78.4, SignalQuality.Live);

        Assert.True(signal.IsUsable);
        Assert.Equal("78", signal.Text);
        Assert.Equal("km/h", signal.Unit);
    }

    [Fact]
    public void SimulatedCountsAsUsableSoTheSyntheticTruckStillDrawsNumbers()
    {
        var signals = new FakeSignals("engine.rpm", "rpm");
        using var signal = new ObservableSignal(signals, "engine.rpm", format: "0");

        signals.Push(1850, SignalQuality.Simulated);

        Assert.True(signal.IsUsable);
        Assert.Equal("1850", signal.Text);
        Assert.Equal(SignalQuality.Simulated, signal.Quality);
    }

    [Fact]
    public void GoingStaleStopsTheNumberBeingRendered()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        using var signal = new ObservableSignal(signals, "vehicle.speed", format: "0");

        signals.Push(78, SignalQuality.Live);
        Assert.Equal("78", signal.Text);

        signals.Push(78, SignalQuality.Stale);

        // The reading is still there for anything that wants it, but Text - what a widget
        // binds to - refuses to present it as current.
        Assert.Equal(78, signal.Value);
        Assert.False(signal.IsUsable);
        Assert.Equal(ObservableSignal.Placeholder, signal.Text);
    }

    [Fact]
    public void EveryRenderedPropertyRaisesChangeNotification()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        using var signal = new ObservableSignal(signals, "vehicle.speed");

        var raised = new List<string>();
        ((INotifyPropertyChanged)signal).PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        signals.Push(42, SignalQuality.Live);

        // A binding that never hears about a change is a display that silently stops
        // updating, which on glass is indistinguishable from one that works.
        Assert.Contains(nameof(ObservableSignal.Text), raised);
        Assert.Contains(nameof(ObservableSignal.Quality), raised);
        Assert.Contains(nameof(ObservableSignal.IsUsable), raised);
    }

    [Fact]
    public void NoticesTheBusMarkingAValueStaleEvenThoughNothingIsPushed()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        using var signal = new ObservableSignal(signals, "vehicle.speed", format: "0");

        signals.Push(78, SignalQuality.Live);
        Assert.Equal("78", signal.Text);

        // VehicleStateBus computes staleness inside Current(); it never publishes the
        // Live -> Stale transition. A signal that only listens to Subscribe therefore sits
        // showing a confident number long after the adapter stopped answering, which is
        // exactly the failure the quality system exists to prevent.
        signals.AgeOut();
        signal.Refresh();

        Assert.Equal(SignalQuality.Stale, signal.Quality);
        Assert.Equal(ObservableSignal.Placeholder, signal.Text);
    }

    [Fact]
    public void DisposingWithdrawsTheDemandSoTheArbiterReclaimsTheBudget()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        var signal = new ObservableSignal(signals, "vehicle.speed");

        Assert.Equal(1, signals.LiveDemands);

        signal.Dispose();

        Assert.Equal(0, signals.LiveDemands);
        Assert.Equal(0, signals.LiveObservers);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var signals = new FakeSignals("vehicle.speed", "km/h");
        var signal = new ObservableSignal(signals, "vehicle.speed");

        signal.Dispose();
        signal.Dispose();

        Assert.Equal(0, signals.LiveDemands);
    }

    /// <summary>
    /// A hand-rolled <see cref="IVehicleSignals"/> that delivers readings synchronously on
    /// the calling thread, so the dispatcher hop is a no-op and the rendering rules can be
    /// asserted without pumping a message loop.
    /// </summary>
    private sealed class FakeSignals(string signalId, string unit) : IVehicleSignals
    {
        private readonly List<Action<SignalValue>> _observers = [];
        private SignalValue _current = SignalValue.Missing(signalId, unit);

        public int LiveDemands { get; private set; }

        public int LiveObservers => _observers.Count;

        public IReadOnlyCollection<string> KnownSignals => [signalId];

        public SignalValue Current(string id) => _current;

        /// <summary>
        /// Age the current reading out without notifying anyone — how the real bus behaves,
        /// since it decides staleness when asked rather than announcing it.
        /// </summary>
        public void AgeOut() => _current = _current.AsStale();

        public IDisposable Subscribe(string id, Action<SignalValue> onValue)
        {
            _observers.Add(onValue);
            return new Disposer(() => _observers.Remove(onValue));
        }

        public ISignalSubscription Require(string id, SignalPriority priority, double rateHz)
        {
            LiveDemands++;
            return new FakeDemand(id, rateHz, () => LiveDemands--);
        }

        public void Push(double value, SignalQuality quality)
        {
            _current = new SignalValue(signalId, value, unit, DateTimeOffset.UnixEpoch, quality);

            foreach (var observer in _observers.ToArray())
            {
                observer(_current);
            }
        }

        private sealed class FakeDemand(string signalId, double rateHz, Action onDispose)
            : ISignalSubscription
        {
            public string SignalId => signalId;

            public double RequestedRateHz => rateHz;

            public double EffectiveRateHz => rateHz;

            public event Action<double>? EffectiveRateChanged
            {
                add { }
                remove { }
            }

            public void Dispose() => onDispose();
        }

        private sealed class Disposer(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
