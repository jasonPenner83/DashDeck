using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Location;

namespace DashDeck.Core.Emulation;

/// <summary>Android Automotive's gear values (<c>VehicleGear</c>): what <c>GEAR_SELECTION</c> holds.</summary>
public enum CarGear
{
    Neutral = 1,
    Reverse = 2,
    Park = 4,
    Drive = 8,
}

/// <summary>
/// What Android is told about the truck (ADR-0075 addendum, feeding Android). Null is "not known":
/// nothing is sent for it, and Android keeps what it was told last.
/// </summary>
public sealed record CarState(
    double? SpeedKmh = null,
    CarGear? Gear = null,
    bool? ParkingBrake = null,
    bool? Night = null,
    double? FuelLitres = null,
    double? TankLitres = null,
    double? EngineRpm = null);

/// <summary>How car data gets into Android Automotive's vehicle HAL.</summary>
public enum VhalRoute
{
    /// <summary>
    /// Written into the vehicle HAL's own store (<c>dumpsys …IVehicle/default --set</c>), so it holds:
    /// the HAL keeps sending it as its current value. Android 13 and later.
    /// </summary>
    Store,

    /// <summary>
    /// Handed to Android's car service as an event (<c>cmd car_service inject-vhal-event</c>). Older
    /// images; a property the HAL keeps publishing can be overwritten by its own value.
    /// </summary>
    Event,
}

/// <summary>
/// Turning the dash's signals into what Android believes about the truck, and into the shell
/// commands that tell it (ADR-0075 addendum). Read-only towards the truck: this writes only to the
/// emulator.
/// </summary>
/// <remarks>
/// The point is <b>Android Automotive's driving restrictions</b>. It limits what can be done on
/// screen — video, keyboards, long lists — while the car moves, and it decides "moving" from speed
/// and gear. Never told, it believes it is parked for ever, and nothing is limited at 100 km/h.
/// <para>
/// Gear comes from the truck when the truck says it (<c>transmission.gearSelector</c>, no source
/// yet). Until then it is <b>worked out, erring towards restricted</b>: moving is Drive; stopped
/// with the engine running is Drive too — idling at a light is not parked; stopped with the engine
/// off, the parking brake on, or the truck silent (the ignition off) while the GPS says still, is
/// Park. With no speed at all, nothing is said.
/// </para>
/// </remarks>
public static class CarFeed
{
    /// <summary>The signals the feed reads — required while Automotive runs, so they are polled.</summary>
    public static readonly IReadOnlyList<string> Signals =
        [Speed, Gear, ParkingBrake, FuelLevel, EngineRpm];

    public const string Speed = "vehicle.speed";
    public const string Gear = "transmission.gearSelector";
    public const string ParkingBrake = "warning.parkingBrake";
    public const string FuelLevel = "fuel.levelPercent";
    public const string EngineRpm = "engine.rpm";

    /// <summary>Faster than this is moving; GPS wanders a couple of km/h standing still.</summary>
    public const double MovingKmh = 3;

    /// <summary>Slower than this the engine is off: a running engine idles at several hundred.</summary>
    private const double EngineOffRpm = 100;

    /// <summary>The vehicle HAL's property ids (<c>VehicleProperty</c>), and their value types.</summary>
    public static class Property
    {
        public const int PerfVehicleSpeed = 0x11600207;   // float, metres a second
        public const int GearSelection = 0x11400400;      // int, CarGear
        public const int ParkingBrakeOn = 0x11200402;     // bool
        public const int NightMode = 0x11200407;          // bool
        public const int FuelLevel = 0x11600307;          // float, millilitres
        public const int InfoFuelCapacity = 0x11600104;   // float, millilitres
        public const int EngineRpmValue = 0x11600305;     // float
    }

    /// <summary>
    /// What Android is to believe, from the newest readings — each used only when it is live, so a
    /// stale speed is never told as the truck's.
    /// </summary>
    /// <param name="read">The newest reading of a signal (<c>IVehicleSignals.Current</c>).</param>
    /// <param name="fix">The truck's position, whose ground speed stands in when the truck's own is not live.</param>
    /// <param name="night">The dash's own answer: the headlights, or the sun.</param>
    /// <param name="tankLitres">The tank's size from the vehicle profile; zero or less when not set.</param>
    public static CarState From(Func<string, SignalValue> read, LocationFix? fix, bool night, double tankLitres)
    {
        ArgumentNullException.ThrowIfNull(read);

        var truckSpeed = SpeedKmh(read(Speed));
        var speed = truckSpeed ?? (fix is { HasFix: true } f && double.IsFinite(f.GroundSpeedKmh) ? Math.Max(0, f.GroundSpeedKmh) : null);
        var brake = read(ParkingBrake) is { IsUsable: true } b && double.IsFinite(b.Value) ? b.Value >= 0.5 : (bool?)null;
        var rpm = read(EngineRpm) is { IsUsable: true } r && double.IsFinite(r.Value) ? Math.Max(0, r.Value) : (double?)null;

        double? fuel = null;
        if (tankLitres > 0 && read(FuelLevel) is { IsUsable: true } level && double.IsFinite(level.Value))
        {
            fuel = Math.Clamp(level.Value, 0, 100) / 100 * tankLitres;
        }

        return new CarState(
            speed,
            GearOf(read(Gear)) ?? WorkedOutGear(speed, brake, rpm, truckSilent: truckSpeed is null),
            brake,
            night,
            fuel,
            tankLitres > 0 ? tankLitres : null,
            rpm);
    }

    private static double? SpeedKmh(SignalValue speed)
    {
        if (!speed.IsUsable || !double.IsFinite(speed.Value))
        {
            return null;
        }

        return Math.Max(0, string.Equals(speed.Unit, "mph", StringComparison.OrdinalIgnoreCase) ? speed.Value * 1.609344 : speed.Value);
    }

    /// <summary>The truck's own gear selector: P, R, N, D, and M driven like D.</summary>
    private static CarGear? GearOf(SignalValue gear) => gear is { IsUsable: true } && double.IsFinite(gear.Value)
        ? (int)Math.Round(gear.Value) switch
        {
            0 => CarGear.Park,
            1 => CarGear.Reverse,
            2 => CarGear.Neutral,
            3 or 4 => CarGear.Drive,
            _ => null,
        }
        : null;

    private static CarGear? WorkedOutGear(double? speed, bool? brake, double? rpm, bool truckSilent) => speed switch
    {
        null => null,
        >= MovingKmh => CarGear.Drive,
        _ when brake == true => CarGear.Park,

        // The truck has stopped answering and the GPS says it is still: the ignition is off.
        _ when truckSilent => CarGear.Park,
        _ when rpm < EngineOffRpm => CarGear.Park,
        _ when rpm is not null => CarGear.Drive,
        _ => null,
    };

    /// <summary>
    /// The shell commands that bring Android from what it was told to what it should now believe —
    /// only what changed enough to matter — and what it will have been told after them.
    /// </summary>
    /// <param name="told">What was sent before, or null when nothing has been.</param>
    public static (IReadOnlyList<string> Commands, CarState Told) Changes(CarState? told, CarState next, AndroidFlavour flavour, VhalRoute route)
    {
        ArgumentNullException.ThrowIfNull(next);

        var was = told ?? new CarState();
        var commands = new List<string>();

        if (flavour != AndroidFlavour.Automotive)
        {
            // A phone or tablet knows nothing of cars; it only follows the dash into the dark.
            if (next.Night is { } dark && dark != was.Night)
            {
                commands.Add(dark ? "cmd uimode night yes" : "cmd uimode night no");
            }

            return (commands, was with { Night = next.Night ?? was.Night });
        }

        string Set(int property, string type, string value) => route == VhalRoute.Store
            ? string.Create(CultureInfo.InvariantCulture, $"dumpsys android.hardware.automotive.vehicle.IVehicle/default --set 0x{property:x8} {type} {value}")
            : string.Create(CultureInfo.InvariantCulture, $"cmd car_service inject-vhal-event 0x{property:x8} {value}");

        static string Float(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        static string Flag(bool value) => value ? "1" : "0";

        var now = was;

        // Capacity before level, so a level is never more than the tank Android was told of.
        if (next.TankLitres is { } tank && !Close(tank, was.TankLitres, 0.5))
        {
            commands.Add(Set(Property.InfoFuelCapacity, "-f", Float(tank * 1000)));
            now = now with { TankLitres = tank };
        }

        if (next.FuelLitres is { } fuel && !Close(fuel, was.FuelLitres, 0.5))
        {
            commands.Add(Set(Property.FuelLevel, "-f", Float(fuel * 1000)));
            now = now with { FuelLitres = fuel };
        }

        // Gear before speed: Drive then moving reads as driving off, not as a parked car at speed.
        if (next.Gear is { } gear && gear != was.Gear)
        {
            commands.Add(Set(Property.GearSelection, "-i", ((int)gear).ToString(CultureInfo.InvariantCulture)));
            now = now with { Gear = gear };
        }

        if (next.ParkingBrake is { } brake && brake != was.ParkingBrake)
        {
            commands.Add(Set(Property.ParkingBrakeOn, "-i", Flag(brake)));
            now = now with { ParkingBrake = brake };
        }

        // Stopping is always told at once; otherwise a change under 1 km/h is not worth a command.
        if (next.SpeedKmh is { } speed && (!Close(speed, was.SpeedKmh, 1) || (speed < MovingKmh) != (was.SpeedKmh < MovingKmh)))
        {
            commands.Add(Set(Property.PerfVehicleSpeed, "-f", Float(speed / 3.6)));
            now = now with { SpeedKmh = speed };
        }

        if (next.EngineRpm is { } rpm && !Close(rpm, was.EngineRpm, 50))
        {
            commands.Add(Set(Property.EngineRpmValue, "-f", Float(rpm)));
            now = now with { EngineRpm = rpm };
        }

        if (next.Night is { } night && night != was.Night)
        {
            commands.Add(Set(Property.NightMode, "-i", Flag(night)));
            now = now with { Night = night };
        }

        return (commands, now);
    }

    /// <summary>
    /// Asks the vehicle HAL for a property it always has: if the answer carries a value, the store
    /// route works on this image.
    /// </summary>
    public static string StoreProbe => string.Create(
        CultureInfo.InvariantCulture, $"dumpsys android.hardware.automotive.vehicle.IVehicle/default --get 0x{Property.PerfVehicleSpeed:x8}");

    /// <summary>True when the probe's answer is a property value, so <see cref="VhalRoute.Store"/> can be used.</summary>
    public static bool StoreAnswers(string output) =>
        (output ?? "").Contains("floatValues", StringComparison.OrdinalIgnoreCase) && !Refused(output);

    /// <summary>
    /// True when Android refused or did not understand a car command. These commands print little
    /// when they work, so success is the absence of their failures.
    /// </summary>
    public static bool Refused(string? output)
    {
        var text = output ?? "";
        string[] failures = ["Caller must", "Can't find service", "fail", "error", "denied", "Unknown", "Invalid", "not supported", "Exception", "usage:"];
        return failures.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Close(double value, double? before, double within) => before is { } b && Math.Abs(value - b) < within;
}
