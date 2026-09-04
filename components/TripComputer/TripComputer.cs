using System.Globalization;
using DashDeck.Abstractions;

namespace TripComputer;

/// <summary>
/// The first component the ecosystem loads: a trip odometer, built through the public SDK.
/// </summary>
/// <remarks>
/// <b>Its job is to be real, not clever.</b> It integrates <c>vehicle.speed</c> over time
/// into a distance, persists that distance as it goes, and picks it back up on the next
/// launch — which exercises every part of the tier-1 contract that matters: it subscribes to
/// a signal by name and never touches a PID (rule 1), it reads the injected
/// <see cref="IClock"/> rather than the wall clock so a replayed drive integrates correctly
/// (ADR-0005), it persists through its own isolated <see cref="IComponentStorage"/>, and it
/// respects the lifecycle — declaring its signal only while started, so an unseen component
/// spends no request budget (ADR-0004, ADR-0015).
/// <para>
/// Headless in this first step: it has no widget yet, so it is a background worker the host
/// runs whenever the vehicle is present. The widget that shows the distance is the next step;
/// the number it will show is already being kept, correctly, here.
/// </para>
/// </remarks>
public sealed class Component : IDashComponent
{
    private const string StorageKey = "trip.km";

    private IComponentContext _context = null!;
    private ISignalSubscription? _demand;
    private IDisposable? _subscription;

    private double _tripKm;
    private DateTimeOffset? _lastReadingAt;

    /// <inheritdoc />
    public string Id => "com.jpenner.tripcomputer";

    /// <inheritdoc />
    public async Task InitializeAsync(IComponentContext context, CancellationToken ct)
    {
        _context = context;

        // Pick up where the last drive left off. A missing or unparseable value is a fresh
        // trip, never a failure — storage is best-effort by contract.
        if (await context.Storage.ReadAsync(StorageKey, ct).ConfigureAwait(false) is { } saved &&
            double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var km))
        {
            _tripKm = km;
        }

        context.Logger.Log(LogLevel.Info, $"trip computer ready at {_tripKm:0.0} km");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        // Declare the signal only while active. The rate is modest: distance integrates fine
        // at 2 Hz and asking for more would spend budget the six gauges in front of the
        // driver have better uses for.
        _demand = _context.Signals.Require("vehicle.speed", SignalPriority.Normal, 2);
        _lastReadingAt = null;
        _subscription = _context.Signals.Subscribe("vehicle.speed", OnSpeed);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        // Withdraw the declaration and stop observing. The distance stays in memory and on
        // disk; only the demand on the adapter goes away.
        _subscription?.Dispose();
        _subscription = null;
        _demand?.Dispose();
        _demand = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SuspendAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken ct) => StartAsync(ct);

    private async void OnSpeed(SignalValue value)
    {
        // Only integrate readings worth trusting. A Stale or Unavailable speed contributes
        // nothing rather than a guessed distance — a confidently wrong odometer is worse than
        // one that pauses (rule 3).
        if (value.Quality is not (SignalQuality.Live or SignalQuality.Simulated))
        {
            _lastReadingAt = null;
            return;
        }

        var now = _context.Clock.UtcNow;

        if (_lastReadingAt is { } last)
        {
            var hours = (now - last).TotalHours;

            // km/h times hours is km. Guard against a clock that jumped or a gap so large it
            // is plainly not one interval — a paused replay should not book a thousand km.
            if (hours > 0 && hours < 1)
            {
                _tripKm += value.Value * hours;
                await Persist().ConfigureAwait(false);
            }
        }

        _lastReadingAt = now;
    }

    private Task Persist() =>
        _context.Storage.WriteAsync(StorageKey, _tripKm.ToString("0.###", CultureInfo.InvariantCulture));
}
