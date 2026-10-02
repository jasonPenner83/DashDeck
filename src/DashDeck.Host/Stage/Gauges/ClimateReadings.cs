namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// What a climate element's reading means (ADR-0040): how many steps a value lights, and whether an
/// indicator is on. Plain arithmetic, apart from the faces that draw it, so it is tested alone.
/// </summary>
public static class ClimateReadings
{
    /// <summary>
    /// How many of <paramref name="steps"/> a value lights, and whether it is the negative side —
    /// a seat at −2 on −3..3 lights two steps in the cooling colour; a fan at 4 on 0..7 lights four.
    /// </summary>
    public static (int Lit, bool Negative) Lit(double value, double min, double max, int steps)
    {
        if (double.IsNaN(value) || steps < 1)
        {
            return (0, false);
        }

        if (value < 0 && min < 0)
        {
            return ((int)Math.Clamp(Math.Round(value / min * steps), 0, steps), true);
        }

        var floor = Math.Max(0, min);
        var span = max - floor;
        return span <= 0 ? (0, false) : ((int)Math.Clamp(Math.Round((value - floor) / span * steps), 0, steps), false);
    }

    /// <summary>
    /// Whether a value lights an indicator: a <c>bit</c> of it set, exactly <c>equals</c>, or at
    /// least <c>onAt</c> (1 unless said) — checked in that order. No value is never on.
    /// </summary>
    public static bool IsOn(GaugeSpec spec, double value)
    {
        if (double.IsNaN(value))
        {
            return false;
        }

        if (spec.Parts.ContainsKey("bit"))
        {
            var bit = (int)Math.Clamp(spec.Number("bit", 0), 0, 62);
            return (((long)Math.Round(value) >> bit) & 1) == 1;
        }

        if (spec.Parts.ContainsKey("equals"))
        {
            return Math.Abs(value - spec.Number("equals", 1)) < 1e-6;
        }

        return value >= spec.Number("onAt", 1);
    }
}
