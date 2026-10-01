using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Discovery;

/// <summary>One entry in the SAE J1979 mode 01 table.</summary>
/// <param name="Pid">The PID.</param>
/// <param name="Id">A suggested catalog id.</param>
/// <param name="Name">What the standard calls it.</param>
/// <param name="Category">The picker group it would sort under.</param>
/// <param name="Decode">
/// The standard's formula, when it is a single linear value the catalog can express; null when
/// the PID is a bitfield, an enum or a multi-value record a decode spec cannot describe.
/// </param>
/// <param name="Min">The standard's lower bound, when there is a decode.</param>
/// <param name="Max">The standard's upper bound, when there is a decode.</param>
public sealed record StandardPid(
    int Pid,
    string Id,
    string Name,
    string Category,
    DecodeSpec? Decode,
    double? Min,
    double? Max)
{
    /// <summary>True when the formula is the standard's, not a placeholder to be filled in.</summary>
    public bool HasDecode => Decode is not null;
}

/// <summary>
/// The legislated mode 01 PIDs, by number, so a scan result can say what a PID <em>is</em>.
/// </summary>
/// <remarks>
/// <b>A starting point, never a reading.</b> These are the published SAE J1979 formulas, used
/// only to pre-fill the definition editor when a scan finds a PID the catalog lacks. Nothing
/// here is ever polled on its own: a suggestion becomes a signal only when someone saves it,
/// and the editor's TEST shows the decoded answer before they do. Where the standard's value
/// is not a single linear number — a bitfield, an enum, a record of several sensors — there is
/// a name and no decode, and the editor says the decode is still to be worked out.
/// </remarks>
public static class StandardPids
{
    private static readonly Dictionary<int, StandardPid> ByPid = Build()
        .ToDictionary(p => p.Pid);

    /// <summary>Every PID the table knows.</summary>
    public static IReadOnlyCollection<StandardPid> All => ByPid.Values;

    public static bool TryGet(int pid, out StandardPid entry) => ByPid.TryGetValue(pid, out entry!);

    /// <summary>What the standard calls a PID, or a plain hex label for one it does not list.</summary>
    public static string NameOf(int pid) =>
        TryGet(pid, out var entry) ? entry.Name : string.Create(CultureInfo.InvariantCulture, $"Mode 01 PID {pid:X2}");

    /// <summary>
    /// A definition to start the editor from — the standard's formula where it has one, a
    /// one-byte raw placeholder where it does not.
    /// </summary>
    public static SignalDefinition Suggest(int pid, CanBus bus)
    {
        if (TryGet(pid, out var entry) && entry.Decode is { } decode)
        {
            return new SignalDefinition
            {
                Id = entry.Id,
                Name = entry.Name,
                Category = entry.Category,
                Bus = bus,
                Pid = (ushort)pid,
                Decode = decode,
                DefaultRateHz = 1,
                Min = entry.Min,
                Max = entry.Max,
            };
        }

        return new SignalDefinition
        {
            Id = entry?.Id ?? string.Create(CultureInfo.InvariantCulture, $"obd2.pid{pid:X2}"),
            Name = NameOf(pid),
            Category = entry?.Category ?? "Other",
            Bus = bus,
            Pid = (ushort)pid,
            Decode = new DecodeSpec(0, 1, false, 1, 0, ""),
            DefaultRateHz = 1,
        };
    }

    // ── The table ─────────────────────────────────────────────────────────────
    // Formulas are J1979's, written as value = raw * scale + offset over the first bytes.

    private const double Pct = 100.0 / 255.0;
    private const double Trim = 100.0 / 128.0;
    private const double Lambda = 2.0 / 65536.0;

    private static StandardPid L(
        int pid, string id, string name, string category,
        int bytes, double scale, double offset, string unit, double min, double max, bool signed = false) =>
        new(pid, id, name, category, new DecodeSpec(0, bytes, signed, scale, offset, unit), min, max);

    private static StandardPid N(int pid, string name, string category) =>
        new(pid, string.Create(CultureInfo.InvariantCulture, $"obd2.pid{pid:X2}"), name, category, null, null, null);

    private static IEnumerable<StandardPid> Build()
    {
        yield return N(0x01, "Monitor Status Since Codes Cleared", "Diagnostics");
        yield return N(0x02, "Freeze Frame Trouble Code", "Diagnostics");
        yield return N(0x03, "Fuel System Status", "Fuel");
        yield return L(0x04, "engine.load", "Calculated Engine Load", "Engine", 1, Pct, 0, "%", 0, 100);
        yield return L(0x05, "engine.coolantTemp", "Engine Coolant Temperature", "Temperature", 1, 1, -40, "°C", -40, 215);
        yield return L(0x06, "fuel.shortTermTrimB1", "Short-Term Fuel Trim, Bank 1", "Fuel", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x07, "fuel.longTermTrimB1", "Long-Term Fuel Trim, Bank 1", "Fuel", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x08, "fuel.shortTermTrimB2", "Short-Term Fuel Trim, Bank 2", "Fuel", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x09, "fuel.longTermTrimB2", "Long-Term Fuel Trim, Bank 2", "Fuel", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x0A, "fuel.pressure", "Fuel Pressure (Gauge)", "Fuel", 1, 3, 0, "kPa", 0, 765);
        yield return L(0x0B, "engine.intakeManifoldPressure", "Intake Manifold Absolute Pressure", "Air & Boost", 1, 1, 0, "kPa", 0, 255);
        yield return L(0x0C, "engine.rpm", "Engine Speed", "Engine", 2, 0.25, 0, "rpm", 0, 16383.75);
        yield return L(0x0D, "vehicle.speed", "Vehicle Speed", "Speed & Distance", 1, 1, 0, "km/h", 0, 255);
        yield return L(0x0E, "engine.timingAdvance", "Timing Advance", "Engine", 1, 0.5, -64, "°", -64, 63.5);
        yield return L(0x0F, "engine.intakeAirTemp", "Intake Air Temperature", "Temperature", 1, 1, -40, "°C", -40, 215);
        yield return L(0x10, "engine.mafRate", "Mass Air Flow Rate", "Air & Boost", 2, 0.01, 0, "g/s", 0, 655.35);
        yield return L(0x11, "engine.throttle", "Throttle Position", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return N(0x12, "Commanded Secondary Air Status", "Air & Boost");
        yield return N(0x13, "Oxygen Sensors Present (2 Banks)", "Emissions");

        for (var n = 1; n <= 8; n++)
        {
            yield return L(0x13 + n, $"oxygen.sensor{n}Voltage", $"Oxygen Sensor {n} Voltage", "Emissions", 1, 0.005, 0, "V", 0, 1.275);
        }

        yield return N(0x1C, "OBD Standard This Vehicle Conforms To", "Diagnostics");
        yield return N(0x1D, "Oxygen Sensors Present (4 Banks)", "Emissions");
        yield return N(0x1E, "Auxiliary Input Status", "Other");
        yield return L(0x1F, "engine.runTime", "Run Time Since Engine Start", "Engine", 2, 1, 0, "s", 0, 65535);
        yield return L(0x21, "vehicle.distanceWithMil", "Distance Travelled with MIL On", "Diagnostics", 2, 1, 0, "km", 0, 65535);
        yield return L(0x22, "fuel.railPressureRelative", "Fuel Rail Pressure (Relative to Manifold Vacuum)", "Fuel", 2, 0.079, 0, "kPa", 0, 5177.265);
        yield return L(0x23, "fuel.railGaugePressure", "Fuel Rail Gauge Pressure", "Fuel", 2, 10, 0, "kPa", 0, 655350);

        for (var n = 1; n <= 8; n++)
        {
            yield return L(0x23 + n, $"oxygen.sensor{n}Lambda", $"Oxygen Sensor {n} Equivalence Ratio", "Emissions", 2, Lambda, 0, "λ", 0, 2);
        }

        yield return L(0x2C, "emissions.commandedEgr", "Commanded EGR", "Air & Boost", 1, Pct, 0, "%", 0, 100);
        yield return L(0x2D, "emissions.egrError", "EGR Error", "Air & Boost", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x2E, "emissions.evapPurge", "Commanded Evaporative Purge", "Fuel", 1, Pct, 0, "%", 0, 100);
        yield return L(0x2F, "fuel.levelPercent", "Fuel Tank Level Input", "Fuel", 1, Pct, 0, "%", 0, 100);
        yield return L(0x30, "engine.warmupsSinceClear", "Warm-ups Since Codes Cleared", "Engine", 1, 1, 0, "", 0, 255);
        yield return L(0x31, "vehicle.distanceSinceClear", "Distance Travelled Since Codes Cleared", "Diagnostics", 2, 1, 0, "km", 0, 65535);
        yield return L(0x32, "fuel.evapVaporPressure", "Evap System Vapour Pressure", "Fuel", 2, 0.25, 0, "Pa", -8192, 8191.75, signed: true);
        yield return L(0x33, "engine.barometricPressure", "Absolute Barometric Pressure", "Air & Boost", 1, 1, 0, "kPa", 0, 255);

        for (var n = 1; n <= 8; n++)
        {
            yield return L(0x33 + n, $"oxygen.sensor{n}LambdaWide", $"Oxygen Sensor {n} Equivalence Ratio (Wide Range)", "Emissions", 2, Lambda, 0, "λ", 0, 2);
        }

        yield return L(0x3C, "engine.catalystTempB1S1", "Catalyst Temperature, Bank 1 Sensor 1", "Temperature", 2, 0.1, -40, "°C", -40, 6513.5);
        yield return L(0x3D, "engine.catalystTempB2S1", "Catalyst Temperature, Bank 2 Sensor 1", "Temperature", 2, 0.1, -40, "°C", -40, 6513.5);
        yield return L(0x3E, "engine.catalystTempB1S2", "Catalyst Temperature, Bank 1 Sensor 2", "Temperature", 2, 0.1, -40, "°C", -40, 6513.5);
        yield return L(0x3F, "engine.catalystTempB2S2", "Catalyst Temperature, Bank 2 Sensor 2", "Temperature", 2, 0.1, -40, "°C", -40, 6513.5);
        yield return N(0x41, "Monitor Status This Drive Cycle", "Diagnostics");
        yield return L(0x42, "vehicle.controlModuleVoltage", "Control Module Voltage", "Electrical", 2, 0.001, 0, "V", 0, 65.535);
        yield return L(0x43, "engine.absoluteLoad", "Absolute Load", "Engine", 2, Pct, 0, "%", 0, 25700);
        yield return L(0x44, "engine.commandedLambda", "Commanded Air-Fuel Equivalence Ratio", "Air & Boost", 2, Lambda, 0, "λ", 0, 2);
        yield return L(0x45, "throttle.relative", "Relative Throttle Position", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x46, "ambient.airTemp", "Ambient Air Temperature", "Temperature", 1, 1, -40, "°C", -40, 215);
        yield return L(0x47, "throttle.absoluteB", "Absolute Throttle Position B", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x48, "throttle.absoluteC", "Absolute Throttle Position C", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x49, "throttle.pedalD", "Accelerator Pedal Position D", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x4A, "throttle.pedalE", "Accelerator Pedal Position E", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x4B, "throttle.pedalF", "Accelerator Pedal Position F", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x4C, "throttle.commanded", "Commanded Throttle Actuator", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x4D, "diagnostics.timeWithMil", "Time Run with MIL On", "Diagnostics", 2, 1, 0, "min", 0, 65535);
        yield return L(0x4E, "diagnostics.timeSinceClear", "Time Since Trouble Codes Cleared", "Diagnostics", 2, 1, 0, "min", 0, 65535);
        yield return N(0x4F, "Maximum Values: Equivalence Ratio, O2 Voltage, Current, MAP", "Other");
        yield return N(0x50, "Maximum Value: Mass Air Flow", "Air & Boost");
        yield return N(0x51, "Fuel Type", "Fuel");
        yield return L(0x52, "fuel.ethanolPercent", "Ethanol Fuel Percentage", "Fuel", 1, Pct, 0, "%", 0, 100);
        yield return L(0x53, "fuel.evapVaporPressureAbs", "Absolute Evap System Vapour Pressure", "Fuel", 2, 0.005, 0, "kPa", 0, 327.675);
        yield return L(0x54, "fuel.evapVaporPressureWide", "Evap System Vapour Pressure (Wide)", "Fuel", 2, 1, 0, "Pa", -32768, 32767, signed: true);
        yield return L(0x55, "oxygen.shortTermSecondaryTrimB1B3", "Short-Term Secondary O2 Trim, Banks 1 and 3", "Emissions", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x56, "oxygen.longTermSecondaryTrimB1B3", "Long-Term Secondary O2 Trim, Banks 1 and 3", "Emissions", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x57, "oxygen.shortTermSecondaryTrimB2B4", "Short-Term Secondary O2 Trim, Banks 2 and 4", "Emissions", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x58, "oxygen.longTermSecondaryTrimB2B4", "Long-Term Secondary O2 Trim, Banks 2 and 4", "Emissions", 1, Trim, -100, "%", -100, 99.2);
        yield return L(0x59, "fuel.railPressureAbsolute", "Fuel Rail Absolute Pressure", "Fuel", 2, 10, 0, "kPa", 0, 655350);
        yield return L(0x5A, "throttle.relativePedal", "Relative Accelerator Pedal Position", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x5B, "electrical.hybridBatteryLife", "Hybrid Battery Pack Remaining Life", "Electrical", 1, Pct, 0, "%", 0, 100);
        yield return L(0x5C, "engine.oilTemp", "Engine Oil Temperature", "Temperature", 1, 1, -40, "°C", -40, 210);
        yield return L(0x5D, "fuel.injectionTiming", "Fuel Injection Timing", "Fuel", 2, 1.0 / 128.0, -210, "°", -210, 301.992);
        yield return L(0x5E, "engine.fuelRate", "Engine Fuel Rate", "Fuel", 2, 0.05, 0, "L/h", 0, 3212.75);
        yield return N(0x5F, "Emission Requirements", "Diagnostics");
        yield return L(0x61, "engine.driverDemandTorque", "Driver's Demanded Engine Torque", "Torque", 1, 1, -125, "%", -125, 130);
        yield return L(0x62, "engine.actualTorque", "Actual Engine Torque", "Torque", 1, 1, -125, "%", -125, 130);
        yield return L(0x63, "engine.referenceTorque", "Engine Reference Torque", "Torque", 2, 1, 0, "Nm", 0, 65535);
        yield return N(0x64, "Engine Percent Torque Data", "Torque");
        yield return N(0x65, "Auxiliary Input / Output Supported", "Other");
        yield return N(0x66, "Mass Air Flow Sensor (A and B)", "Air & Boost");
        yield return N(0x67, "Engine Coolant Temperature (Sensors 1 and 2)", "Temperature");
        yield return N(0x68, "Intake Air Temperature Sensor", "Temperature");
        yield return N(0x69, "Commanded EGR and EGR Error", "Air & Boost");
        yield return N(0x6A, "Commanded Diesel Intake Air Flow Control", "Air & Boost");
        yield return N(0x6B, "Exhaust Gas Recirculation Temperature", "Temperature");
        yield return N(0x6C, "Commanded Throttle Actuator Control and Position", "Throttle & Pedal");
        yield return N(0x6D, "Fuel Pressure Control System", "Fuel");
        yield return N(0x6E, "Injection Pressure Control System", "Fuel");
        yield return N(0x6F, "Turbocharger Compressor Inlet Pressure", "Air & Boost");
        yield return N(0x70, "Boost Pressure Control", "Air & Boost");
        yield return N(0x71, "Variable Geometry Turbo Control", "Air & Boost");
        yield return N(0x72, "Wastegate Control", "Air & Boost");
        yield return N(0x73, "Exhaust Pressure", "Emissions");
        yield return N(0x74, "Turbocharger RPM", "Engine");
        yield return N(0x75, "Turbocharger Temperature (Turbo A)", "Temperature");
        yield return N(0x76, "Turbocharger Temperature (Turbo B)", "Temperature");
        yield return N(0x77, "Charge Air Cooler Temperature", "Temperature");
        yield return N(0x78, "Exhaust Gas Temperature, Bank 1", "Temperature");
        yield return N(0x79, "Exhaust Gas Temperature, Bank 2", "Temperature");
        yield return N(0x7A, "Diesel Particulate Filter (Bank 1)", "Diagnostics");
        yield return N(0x7B, "Diesel Particulate Filter (Bank 2)", "Diagnostics");
        yield return N(0x7C, "Diesel Particulate Filter Temperature", "Temperature");
        yield return N(0x7D, "NOx NTE Control Area Status", "Diagnostics");
        yield return N(0x7E, "PM NTE Control Area Status", "Diagnostics");
        yield return N(0x7F, "Engine Run Time", "Engine");
        yield return N(0x81, "Engine Run Time for AECD #1–#5", "Engine");
        yield return N(0x82, "Engine Run Time for AECD #6–#10", "Engine");
        yield return N(0x83, "NOx Sensor", "Diagnostics");
        yield return L(0x84, "engine.manifoldSurfaceTemp", "Manifold Surface Temperature", "Temperature", 1, 1, -40, "°C", -40, 215);
        yield return N(0x85, "NOx Reagent System", "Diagnostics");
        yield return N(0x86, "Particulate Matter Sensor", "Diagnostics");
        yield return N(0x87, "Intake Manifold Absolute Pressure (Sensors A and B)", "Air & Boost");
        yield return N(0x88, "SCR Inducement System", "Diagnostics");
        yield return N(0x89, "Run Time for AECD #11–#15", "Engine");
        yield return N(0x8A, "Run Time for AECD #16–#20", "Engine");
        yield return N(0x8B, "Diesel Aftertreatment", "Diagnostics");
        yield return N(0x8C, "O2 Sensor (Wide Range)", "Emissions");
        yield return L(0x8D, "throttle.positionG", "Throttle Position G", "Throttle & Pedal", 1, Pct, 0, "%", 0, 100);
        yield return L(0x8E, "engine.frictionTorque", "Engine Friction Percent Torque", "Torque", 1, 1, -125, "%", -125, 130);
        yield return N(0x8F, "PM Sensor, Banks 1 and 2", "Diagnostics");
        yield return N(0x90, "WWH-OBD Vehicle OBD System Information", "Diagnostics");
        yield return N(0x91, "WWH-OBD Vehicle OBD System Information", "Diagnostics");
        yield return N(0x92, "Fuel System Control", "Fuel");
        yield return N(0x93, "WWH-OBD Vehicle OBD Counters Support", "Diagnostics");
        yield return N(0x94, "NOx Warning and Inducement System", "Diagnostics");
        yield return N(0x98, "Exhaust Gas Temperature Sensor (Bank 1)", "Temperature");
        yield return N(0x99, "Exhaust Gas Temperature Sensor (Bank 2)", "Temperature");
        yield return N(0x9A, "Hybrid/EV Vehicle System Data", "Electrical");
        yield return N(0x9B, "Diesel Exhaust Fluid Sensor Data", "Diagnostics");
        yield return N(0x9C, "O2 Sensor Data", "Emissions");
        yield return N(0x9D, "Engine Fuel Rate (Mass)", "Fuel");
        yield return N(0x9E, "Engine Exhaust Flow Rate", "Air & Boost");
        yield return N(0x9F, "Fuel System Percentage Use", "Fuel");
        yield return N(0xA1, "NOx Sensor Corrected Data", "Diagnostics");
        yield return N(0xA2, "Cylinder Fuel Rate", "Fuel");
        yield return N(0xA3, "Evap System Vapour Pressure", "Fuel");
        yield return N(0xA4, "Transmission Actual Gear", "Drivetrain");
        yield return N(0xA5, "Commanded Diesel Exhaust Fluid Dosing", "Diagnostics");
        yield return L(0xA6, "vehicle.odometer", "Odometer", "Speed & Distance", 4, 0.1, 0, "km", 0, 429496729.5);
    }
}
