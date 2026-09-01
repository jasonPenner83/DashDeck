namespace DashDeck.Abstractions;

/// <summary>
/// A live declaration that a component needs a signal. Disposing it withdraws the demand.
/// </summary>
/// <remarks>
/// <see cref="EffectiveRateHz"/> may be lower than what was asked for. Components are told
/// rather than silently starved, so a component can honestly render "1 Hz" instead of
/// looking frozen.
/// </remarks>
public interface ISignalSubscription : IDisposable
{
    /// <summary>The signal this declaration is for.</summary>
    string SignalId { get; }

    /// <summary>What was asked for.</summary>
    double RequestedRateHz { get; }

    /// <summary>What the arbiter can actually deliver right now. Zero means fully shed.</summary>
    double EffectiveRateHz { get; }

    /// <summary>Raised when the arbiter re-plans and this declaration's allocation changes.</summary>
    event Action<double>? EffectiveRateChanged;
}
