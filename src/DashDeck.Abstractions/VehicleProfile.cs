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

    // ── apiVersion 1.2: what the vehicle is, decoded from its VIN (ADR-0033) ──
    // Read at launch from the user's cached decode, corrected by hand where the decoder was
    // wrong. Zero, empty and null all mean "not known" — a component must cope with each.
    // The VIN itself is deliberately not here: no component needs the number to use the facts.

    /// <summary>Model year, e.g. 2019. Zero when not known.</summary>
    public int ModelYear { get; init; }

    /// <summary>Manufacturer as the decoder names it, e.g. <c>FORD</c>. Empty when not known.</summary>
    public string Make { get; init; } = "";

    /// <summary>Model, e.g. <c>F-150</c>. Empty when not known.</summary>
    public string Model { get; init; } = "";

    /// <summary>Trim level, e.g. <c>XLT</c>. Empty when not known — the VIN rarely says.</summary>
    public string Trim { get; init; } = "";

    /// <summary>
    /// Engine displacement in litres, e.g. 2.7. Zero when not known — and for an electric
    /// vehicle, which has none.
    /// </summary>
    public double EngineDisplacementLitres { get; init; }

    /// <summary>Number of cylinders. Zero when not known.</summary>
    public int EngineCylinders { get; init; }

    /// <summary>True for a turbocharged engine, false for one that is not, null when not known.</summary>
    public bool? Turbocharged { get; init; }

    /// <summary>Primary fuel, e.g. <c>Gasoline</c> or <c>Diesel</c>. Empty when not known.</summary>
    public string FuelType { get; init; } = "";

    /// <summary>Nothing set — what a component sees on a host that provides no profile.</summary>
    public static VehicleProfile Empty { get; } = new();
}
