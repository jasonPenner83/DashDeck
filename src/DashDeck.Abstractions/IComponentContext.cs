namespace DashDeck.Abstractions;

/// <summary>
/// Everything the host lends a component. Handed over once, in
/// <see cref="IDashComponent.InitializeAsync"/>.
/// </summary>
/// <remarks>
/// The surface is deliberately narrow: a component reaches the outside world only through
/// what is here, which is what lets the host know — and bound — what a component can do.
/// <para>
/// Two guarantees are expressed in the type system rather than in prose, because a rule the
/// compiler enforces cannot be forgotten:
/// </para>
/// <list type="bullet">
///   <item>
///     <see cref="Clock"/> is the only clock. A component reading <c>DateTime.Now</c>
///     cannot be replayed against a scripted drive, so time is supplied as data (ADR-0005).
///   </item>
///   <item>
///     <see cref="Actions"/> is <see langword="null"/> until a write permission is granted,
///     which is never before Phase 3 (ADR-0006). Absence of permission is a null reference,
///     not a runtime exception — a component physically cannot call a write it was not given.
///   </item>
/// </list>
/// </remarks>
public interface IComponentContext
{
    /// <summary>Named-signal access. Never touches CAN; subscribe and require by id.</summary>
    IVehicleSignals Signals { get; }

    /// <summary>
    /// Persistence scoped to this component alone. It cannot see another component's data,
    /// and another component cannot see its.
    /// </summary>
    IComponentStorage Storage { get; }

    /// <summary>The component's own settings, as the user has configured them.</summary>
    IComponentSettings Settings { get; }

    /// <summary>Where a component's diagnostics go. Tagged with its id by the host.</summary>
    IComponentLogger Logger { get; }

    /// <summary>The injected clock. Never <c>DateTime.Now</c>, so replay and tests hold.</summary>
    IClock Clock { get; }

    /// <summary>
    /// Vehicle writes, or <see langword="null"/> when none are granted — which is always,
    /// for now (ADR-0006, read-only until Phase 3). Kept in the contract so the shape a
    /// granted component sees is stable, and so the guarantee is a nullable reference.
    /// </summary>
    IVehicleActions? Actions => null;

    /// <summary>
    /// Facts about the truck the app is fitted to — the fuel tank's size today, more later —
    /// set by the user in Settings (ADR-0029). A value that depends on the vehicle but cannot
    /// be read off the bus reads it here. Defaults to <see cref="VehicleProfile.Empty"/> on a
    /// host that provides none, so a component built for it degrades rather than breaks.
    /// </summary>
    VehicleProfile Vehicle => VehicleProfile.Empty;
}
