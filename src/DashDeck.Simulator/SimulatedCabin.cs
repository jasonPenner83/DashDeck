namespace DashDeck.Simulator;

/// <summary>
/// The switches and doors of the synthetic truck, set by hand — what the ID hunter's guide asks
/// a person to do, done for them when it runs against the simulator (ADR-0044).
/// </summary>
/// <remarks>
/// The synthetic truck broadcasts these on frames whose identifiers are <b>invented</b> (see
/// <see cref="SyntheticTransport"/>): they exist so the hunter's listen-and-rank path can be
/// exercised at a desk, and are no claim about where a real Ford puts anything.
/// </remarks>
public sealed class SimulatedCabin
{
    public bool DriverDoorOpen { get; set; }

    public bool PassengerDoorOpen { get; set; }

    /// <summary>Heat 1–3 positive, cooling 1–3 negative, 0 off.</summary>
    public int DriverSeat { get; set; }

    public int PassengerSeat { get; set; }

    public bool WheelHeat { get; set; }

    public int Fan { get; set; } = 3;

    public bool AirConditioning { get; set; } = true;

    public bool Recirculate { get; set; }

    public bool RearDefrost { get; set; }

    public bool Auto { get; set; } = true;

    public double DriverSetTempC { get; set; } = 21.5;

    public bool DriverSeatbeltBuckled { get; set; } = true;

    public bool ParkingBrake { get; set; } = true;

    /// <summary>Extra engine speed while parked — the throttle blip a guide asks for.</summary>
    public double ExtraRpm { get; set; }

    /// <summary>Every name <see cref="Set"/> knows.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "driverDoor", "passengerDoor", "driverSeat", "passengerSeat", "wheelHeat", "fan", "ac",
        "recirc", "rearDefrost", "auto", "driverSetTemp", "driverSeatbelt", "parkingBrake", "rev",
    ];

    /// <summary>Set one control by name, as a guide's step names it. False for a name it does not know.</summary>
    public bool Set(string name, double value)
    {
        var on = value != 0;
        switch (name)
        {
            case "driverDoor": DriverDoorOpen = on; break;
            case "passengerDoor": PassengerDoorOpen = on; break;
            case "driverSeat": DriverSeat = (int)value; break;
            case "passengerSeat": PassengerSeat = (int)value; break;
            case "wheelHeat": WheelHeat = on; break;
            case "fan": Fan = (int)value; break;
            case "ac": AirConditioning = on; break;
            case "recirc": Recirculate = on; break;
            case "rearDefrost": RearDefrost = on; break;
            case "auto": Auto = on; break;
            case "driverSetTemp": DriverSetTempC = value; break;
            case "driverSeatbelt": DriverSeatbeltBuckled = on; break;
            case "parkingBrake": ParkingBrake = on; break;
            case "rev": ExtraRpm = value; break;
            default: return false;
        }

        return true;
    }
}
