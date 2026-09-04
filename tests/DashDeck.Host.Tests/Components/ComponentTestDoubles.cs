using DashDeck.Abstractions;

namespace DashDeck.Host.Tests.Components;

/// <summary>A signals source that knows a fixed set of ids and supplies nothing.</summary>
internal sealed class FakeSignals(params string[] known) : IVehicleSignals
{
    public IReadOnlyCollection<string> KnownSignals { get; } = known;

    public SignalValue Current(string signalId) => SignalValue.Missing(signalId);

    public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) => new Noop();

    public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz) =>
        new NoopSubscription(signalId, rateHz);

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoopSubscription(string id, double rate) : ISignalSubscription
    {
        public string SignalId => id;
        public double RequestedRateHz => rate;
        public double EffectiveRateHz => rate;
        public event Action<double>? EffectiveRateChanged { add { } remove { } }
        public void Dispose() { }
    }
}

/// <summary>A clock frozen at a fixed instant. Never the wall clock.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}

/// <summary>A logger that keeps its lines, so a test can assert what was recorded.</summary>
internal sealed class CapturingLogger : IComponentLogger
{
    public List<string> Lines { get; } = [];

    public void Log(LogLevel level, string message, Exception? exception = null) =>
        Lines.Add($"{level}: {message}");
}

/// <summary>
/// A component whose every method is programmable, for testing the guard around it.
/// </summary>
internal sealed class ScriptedComponent : IDashComponent
{
    public string Id { get; init; } = "com.test.scripted";

    /// <summary>What <see cref="StartAsync"/> does. Default: nothing, successfully.</summary>
    public Func<CancellationToken, Task>? OnStart { get; init; }

    /// <summary>How many times <see cref="StartAsync"/> was actually entered.</summary>
    public int StartCalls { get; private set; }

    public Task InitializeAsync(IComponentContext context, CancellationToken ct) => Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        StartCalls++;
        return OnStart?.Invoke(ct) ?? Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SuspendAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
}
