namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// The arithmetic behind the compass and the G meter (ADR-0039), kept free of WPF so it can be
/// tested anywhere. Moved here from the old compass view model when the compass became stage
/// layout elements; the behaviour is unchanged.
/// </summary>
public static class SensorMath
{
    /// <summary>
    /// The compass point, to eight of them.
    /// </summary>
    /// <remarks>
    /// Sixteen would be more precise and less readable at a glance, and the number is right
    /// beside it for anyone who wants precision. The half-sector bias is what makes each point
    /// own the 45° <em>centred</em> on it rather than the 45° starting at it — without it, due
    /// north reads as north-east.
    /// </remarks>
    public static string Cardinal(double degrees)
    {
        if (double.IsNaN(degrees))
        {
            return "——";
        }

        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

        var normalised = ((degrees % 360) + 360) % 360;
        return points[(int)Math.Floor(((normalised + 22.5) % 360) / 45)];
    }

    /// <summary>
    /// Ease towards a new bearing.
    /// </summary>
    /// <remarks>
    /// Averaged as a <em>vector</em>, not as a number. Averaging degrees directly puts the
    /// mean of 350° and 10° at 180° — a compass that swings to due south every time it crosses
    /// north. Converting to a unit vector and back is the fix.
    /// </remarks>
    public static double SmoothAngle(double previous, double next, double factor = 0.25)
    {
        if (double.IsNaN(previous))
        {
            return next;
        }

        var p = previous * Math.PI / 180;
        var n = next * Math.PI / 180;

        var x = (Math.Cos(p) * (1 - factor)) + (Math.Cos(n) * factor);
        var y = (Math.Sin(p) * (1 - factor)) + (Math.Sin(n) * factor);

        return ((Math.Atan2(y, x) * 180 / Math.PI) + 360) % 360;
    }

    /// <summary>
    /// Light smoothing. An accelerometer in a vehicle picks up the road surface as well as the
    /// driving, and a ball that vibrates is unreadable at exactly the moment it matters.
    /// </summary>
    public static double Smooth(double previous, double next, double factor = 0.35) =>
        (previous * (1 - factor)) + (next * factor);

    /// <summary>Total g from its two components.</summary>
    public static double Total(double lateral, double longitudinal) =>
        Math.Sqrt((lateral * lateral) + (longitudinal * longitudinal));

    /// <summary>
    /// Where the G meter's ball sits, in pixels from the centre, for a meter whose outer ring is
    /// <paramref name="range"/> g at <paramref name="radius"/> px.
    /// </summary>
    /// <remarks>
    /// <b>The ball moves the way the driver is pushed</b>, not the way the truck accelerates — so
    /// braking throws it forward, towards the top, and a right-hand bend throws it left. That is
    /// the convention every G meter in a car uses, and the one that matches what the body already
    /// feels. Clamped to the outer ring, so a reading past the range parks on the edge rather than
    /// leaving the meter.
    /// </remarks>
    public static (double X, double Y) Ball(double lateral, double longitudinal, double range, double radius)
    {
        var perG = radius / range;
        var x = -lateral * perG;
        var y = longitudinal * perG;
        var distance = Math.Sqrt((x * x) + (y * y));

        if (distance > radius)
        {
            x *= radius / distance;
            y *= radius / distance;
        }

        return (x, y);
    }
}
