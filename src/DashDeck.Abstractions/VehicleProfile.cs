namespace DashDeck.Abstractions;

/// <summary>
/// Facts about the truck the app is fitted to — set by the user, depended on by a value that
/// cannot read them off the bus.
/// </summary>
/// <remarks>
/// The fuel tank's size is the first: a range estimate needs it and no PID reports it. It lives
/// on the context (ADR-0029) rather than as a per-component setting because it is a property of
/// the <em>vehicle</em>, not of any one component — the day a second component wants the tank
/// size, or the truck's weight, or a wheel circumference, it reads the same profile. Additive by
/// design: new facts are new fields, and a component that does not read one is unaffected.
/// </remarks>
public sealed record VehicleProfile
{
    /// <summary>Usable fuel tank, litres. Zero or negative means the user has not set it.</summary>
    public double FuelTankLitres { get; init; }

    /// <summary>Nothing set — what a component sees on a host that provides no profile.</summary>
    public static VehicleProfile Empty { get; } = new();
}
