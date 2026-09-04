namespace DashDeck.Abstractions;

/// <summary>
/// Writes to the vehicle. Empty on purpose, and will stay empty until Phase 3.
/// </summary>
/// <remarks>
/// <b>The type exists so its absence has a shape.</b> <see cref="IComponentContext.Actions"/>
/// is of this type and is null until a write permission is granted — which is never, before
/// Phase 3 (ADR-0006). Defining the interface now, with nothing on it, lets the read-only
/// posture be a fact about the type system rather than a promise in a comment: there is no
/// method to call because none has been added.
/// <para>
/// When writes arrive, each one passes the five gates in ADR-0006 — all five, or it does not
/// ship — and each is added here deliberately, never in passing. The truck must work without
/// us (ADR-0006); this interface is where that stops being automatic, so it is guarded.
/// </para>
/// </remarks>
public interface IVehicleActions
{
    // Intentionally empty until Phase 3. See remarks.
}
