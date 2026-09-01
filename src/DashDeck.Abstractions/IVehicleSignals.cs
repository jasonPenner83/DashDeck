namespace DashDeck.Abstractions;

/// <summary>
/// The only way a component touches vehicle data.
/// </summary>
/// <remarks>
/// Components subscribe to named signals and know nothing about PIDs, adapters,
/// transports or buses. That indirection is what lets the physical link change — from a
/// synthetic truck, to a USB adapter, to a raw CAN interface — without any component
/// changing at all.
/// <para>
/// Note the split between <see cref="Require"/> and <see cref="Subscribe"/>: requiring a
/// signal creates demand the arbiter schedules, while subscribing only observes whatever
/// is already flowing. A component that subscribes without requiring will receive
/// values only if something else asked for them.
/// </para>
/// </remarks>
public interface IVehicleSignals
{
    /// <summary>Signal ids the loaded catalog defines.</summary>
    IReadOnlyCollection<string> KnownSignals { get; }

    /// <summary>
    /// The most recent reading, or an <see cref="SignalQuality.Unavailable"/> value if none
    /// has arrived. Never returns null — there is always something to render.
    /// </summary>
    SignalValue Current(string signalId);

    /// <summary>Observe every new reading of a signal. Dispose to stop observing.</summary>
    IDisposable Subscribe(string signalId, Action<SignalValue> onValue);

    /// <summary>
    /// Declare that this signal is needed, at roughly this rate. The arbiter merges every
    /// live declaration into one polling plan. Dispose to withdraw the demand.
    /// </summary>
    ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz);
}
