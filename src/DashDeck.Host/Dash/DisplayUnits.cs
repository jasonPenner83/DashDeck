using System.Globalization;

namespace DashDeck.Host.Dash;

/// <summary>What unit a card shows its value in.</summary>
public enum DisplayUnit
{
    /// <summary>Whatever the catalog reports. Always available, always correct.</summary>
    Auto,

    Celsius,
    Fahrenheit,
    KilometresPerHour,
    MilesPerHour,
}

/// <summary>
/// Converts a reading for display only.
/// </summary>
/// <remarks>
/// <b>The conversion happens at the very edge, and nothing below the card learns about it.</b>
/// The catalog decodes what the truck actually sends — km/h and °C — and the signal, the
/// bus, the arbiter and the state that gets recorded and replayed all stay in those units.
/// A card asking for °F changes six characters of rendering, not the meaning of the data.
/// <para>
/// That is deliberate: a unit preference that reached into the catalog would make a recorded
/// drive un-replayable against a differently configured dash, and would put a conversion
/// between the decode spec and the range check in <c>SignalDefinition.InRange</c> — which is
/// exactly the sort of thing that turns a wrong decode into a plausible-looking number.
/// </para>
/// </remarks>
public static class DisplayUnits
{
    /// <summary>The symbol shown for each unit. Auto has none of its own — it borrows.</summary>
    public static string Symbol(DisplayUnit unit, string catalogUnit) => unit switch
    {
        DisplayUnit.Celsius => "°C",
        DisplayUnit.Fahrenheit => "°F",
        DisplayUnit.KilometresPerHour => "km/h",
        DisplayUnit.MilesPerHour => "mph",
        _ => catalogUnit,
    };

    /// <summary>A short label for the editor, e.g. <c>°F</c> or <c>AUTO</c>.</summary>
    public static string Caption(DisplayUnit unit) =>
        unit is DisplayUnit.Auto ? "AUTO" : Symbol(unit, "");

    /// <summary>
    /// Which units a card on this signal may offer.
    /// </summary>
    /// <remarks>
    /// Driven by the catalog's unit rather than by the signal id, so a Ford PID discovered
    /// later gets the right choices for free provided its decode spec is honest.
    /// </remarks>
    public static IReadOnlyList<DisplayUnit> ChoicesFor(string catalogUnit) => catalogUnit switch
    {
        "°C" or "°F" => [DisplayUnit.Auto, DisplayUnit.Celsius, DisplayUnit.Fahrenheit],
        "km/h" or "mph" => [DisplayUnit.Auto, DisplayUnit.KilometresPerHour, DisplayUnit.MilesPerHour],
        _ => [DisplayUnit.Auto],
    };

    /// <summary>True when this signal has anything to offer beyond Auto.</summary>
    public static bool IsConvertible(string catalogUnit) => ChoicesFor(catalogUnit).Count > 1;

    /// <summary>
    /// Convert a value from the catalog's unit into the requested one.
    /// </summary>
    /// <remarks>
    /// A conversion that does not apply returns the value untouched. Asking for mph on a
    /// temperature is a configuration mistake, not a reason to render nonsense.
    /// </remarks>
    public static double Convert(double value, string catalogUnit, DisplayUnit to)
    {
        if (double.IsNaN(value) || to is DisplayUnit.Auto)
        {
            return value;
        }

        return (catalogUnit, to) switch
        {
            ("°C", DisplayUnit.Fahrenheit) => (value * 9.0 / 5.0) + 32.0,
            ("°F", DisplayUnit.Celsius) => (value - 32.0) * 5.0 / 9.0,
            ("km/h", DisplayUnit.MilesPerHour) => value * 0.621371,
            ("mph", DisplayUnit.KilometresPerHour) => value / 0.621371,
            _ => value,
        };
    }

    /// <summary>Convert and format in one step — what a card actually needs.</summary>
    public static string Format(double value, string catalogUnit, DisplayUnit to, string format) =>
        Convert(value, catalogUnit, to).ToString(format, CultureInfo.CurrentCulture);
}
