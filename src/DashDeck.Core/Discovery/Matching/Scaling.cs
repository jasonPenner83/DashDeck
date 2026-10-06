using System.Globalization;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>Which bytes of an answer make the number: where, how many, and whether signed.</summary>
public readonly record struct RawWindow(int Offset, int Length, bool Signed)
{
    /// <summary>Read the number from an answer, or null when the answer is too short.</summary>
    public long? Read(ReadOnlySpan<byte> payload)
    {
        if (Offset + Length > payload.Length)
        {
            return null;
        }

        long raw = 0;
        for (var i = 0; i < Length; i++)
        {
            raw = (raw << 8) | payload[Offset + i];
        }

        if (Signed)
        {
            var signBit = 1L << ((Length * 8) - 1);
            if ((raw & signBit) != 0)
            {
                raw -= 1L << (Length * 8);
            }
        }

        return raw;
    }

    /// <summary>The bytes as letters, the way people write Ford formulas: <c>A</c>, <c>(A·256+B)</c>.</summary>
    public string Letters
    {
        get
        {
            var a = (char)('A' + Offset);
            var text = Length == 1 ? a.ToString() : $"({a}·256+{(char)(a + 1)})";
            return Signed ? $"signed {text}" : text;
        }
    }

    /// <summary>Every window worth trying on an answer this long: each byte, and each pair, unsigned and signed.</summary>
    public static IEnumerable<RawWindow> For(int payloadLength)
    {
        for (var length = 1; length <= 2; length++)
        {
            for (var offset = 0; offset + length <= payloadLength; offset++)
            {
                yield return new RawWindow(offset, length, false);
                yield return new RawWindow(offset, length, true);
            }
        }
    }
}

/// <summary>The unit FORScan showed a value in.</summary>
public enum ShownUnit
{
    None,
    Celsius,
    Fahrenheit,
    Kpa,
    Psi,
    Bar,
    InHg,
    Kmh,
    Mph,
    Rpm,
    Percent,
    Volt,
    LitresPerHour,
    GallonsPerHour,
    Litres,
    Gallons,
    Km,
    Miles,
    Degrees,
    Ms,

    /// <summary>A switch: FORScan shows On / Off, Yes / No, Open / Closed (ADR-0050).</summary>
    OnOff,
}

/// <summary>Turns what FORScan showed into the metric unit the catalog stores.</summary>
public static class UnitConversion
{
    /// <summary>The value in the catalog's unit, the factor between them (for tolerances), and that unit's name.</summary>
    public static (double Value, double Factor, string Unit) ToMetric(double shown, ShownUnit unit) => unit switch
    {
        ShownUnit.Fahrenheit => ((shown - 32) * 5 / 9, 5.0 / 9, "°C"),
        ShownUnit.Celsius => (shown, 1, "°C"),
        ShownUnit.Psi => (shown * 6.894757, 6.894757, "kPa"),
        ShownUnit.Bar => (shown * 100, 100, "kPa"),
        ShownUnit.InHg => (shown * 3.386389, 3.386389, "kPa"),
        ShownUnit.Kpa => (shown, 1, "kPa"),
        ShownUnit.Mph => (shown * 1.609344, 1.609344, "km/h"),
        ShownUnit.Kmh => (shown, 1, "km/h"),
        ShownUnit.GallonsPerHour => (shown * 3.785411784, 3.785411784, "L/h"),
        ShownUnit.LitresPerHour => (shown, 1, "L/h"),
        ShownUnit.Gallons => (shown * 3.785411784, 3.785411784, "L"),
        ShownUnit.Litres => (shown, 1, "L"),
        ShownUnit.Miles => (shown * 1.609344, 1.609344, "km"),
        ShownUnit.Km => (shown, 1, "km"),
        ShownUnit.Rpm => (shown, 1, "rpm"),
        ShownUnit.Percent => (shown, 1, "%"),
        ShownUnit.Volt => (shown, 1, "V"),
        ShownUnit.Degrees => (shown, 1, "°"),
        ShownUnit.Ms => (shown, 1, "ms"),
        ShownUnit.OnOff => (shown, 1, ""),
        _ => (shown, 1, ""),
    };

    /// <summary>The unit a log heading names — "°F", "psi", "km/h" — or <see cref="ShownUnit.None"/>.</summary>
    public static ShownUnit Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "°c" or "c" or "degc" or "deg c" => ShownUnit.Celsius,
        "°f" or "f" or "degf" or "deg f" => ShownUnit.Fahrenheit,
        "kpa" => ShownUnit.Kpa,
        "psi" => ShownUnit.Psi,
        "bar" => ShownUnit.Bar,
        "inhg" or "in hg" => ShownUnit.InHg,
        "km/h" or "kph" or "kmh" => ShownUnit.Kmh,
        "mph" => ShownUnit.Mph,
        "rpm" or "1/min" => ShownUnit.Rpm,
        "%" => ShownUnit.Percent,
        "v" => ShownUnit.Volt,
        "l/h" or "lph" => ShownUnit.LitresPerHour,
        "gal/h" or "gph" => ShownUnit.GallonsPerHour,
        "l" => ShownUnit.Litres,
        "gal" => ShownUnit.Gallons,
        "km" => ShownUnit.Km,
        "mi" or "miles" => ShownUnit.Miles,
        "°" or "deg" => ShownUnit.Degrees,
        "ms" => ShownUnit.Ms,
        "on/off" or "on / off" or "state" or "bool" => ShownUnit.OnOff,
        _ => ShownUnit.None,
    };

    /// <summary>The short label for a unit, as FORScan prints it.</summary>
    public static string Label(ShownUnit unit) => unit switch
    {
        ShownUnit.Celsius => "°C",
        ShownUnit.Fahrenheit => "°F",
        ShownUnit.Kpa => "kPa",
        ShownUnit.Psi => "psi",
        ShownUnit.Bar => "bar",
        ShownUnit.InHg => "inHg",
        ShownUnit.Kmh => "km/h",
        ShownUnit.Mph => "mph",
        ShownUnit.Rpm => "rpm",
        ShownUnit.Percent => "%",
        ShownUnit.Volt => "V",
        ShownUnit.LitresPerHour => "L/h",
        ShownUnit.GallonsPerHour => "gal/h",
        ShownUnit.Litres => "L",
        ShownUnit.Gallons => "gal",
        ShownUnit.Km => "km",
        ShownUnit.Miles => "mi",
        ShownUnit.Degrees => "°",
        ShownUnit.Ms => "ms",
        ShownUnit.OnOff => "on / off",
        _ => "(no unit)",
    };

    /// <summary>
    /// A switch's state as FORScan words it — on, yes, open, ajar, active… — or null for anything that
    /// is not one. Numbers are not states here: 1 and 0 are read as numbers unless the unit says on / off.
    /// </summary>
    public static bool? ParseState(string text) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "yes" or "true" or "open" or "opened" or "ajar" or "active" or "engaged" or "pressed" or "set" or "fastened" or "lit" => true,
        "off" or "no" or "false" or "closed" or "close" or "shut" or "inactive" or "disengaged" or "released" or "clear" or "unfastened" or "not lit" => false,
        _ => null,
    };

    /// <summary>
    /// How far off a typed value may be: half its last digit (FORScan rounds what it shows), and
    /// never less than half a percent.
    /// </summary>
    public static double Tolerance(string typed, double value)
    {
        var text = typed.Trim().Replace(',', '.');
        var dot = text.IndexOf('.');
        var decimals = dot < 0 ? 0 : text.Length - dot - 1;
        var step = Math.Pow(10, -decimals);
        return Math.Max(step * 0.51, Math.Abs(value) * 0.005);
    }
}

/// <summary>One value as FORScan showed it, beside the answer that was in force at that moment.</summary>
/// <param name="Value">In the catalog's metric unit.</param>
/// <param name="Tolerance">How far off it may be, in the same unit.</param>
public sealed record Sample(DateTimeOffset At, byte[] Payload, double Value, double Tolerance);

/// <summary>A way to read an identifier that agrees with what FORScan showed.</summary>
/// <param name="Fitted">True when the scale and offset were fitted to the samples rather than taken from the usual ones.</param>
/// <param name="DistinctRaw">How many different raw numbers the samples covered — evidence, beyond one.</param>
/// <param name="R2">How well a series fitted (1 is perfect); null for typed samples.</param>
public sealed record ScalingCandidate(
    RawWindow Window,
    double Scale,
    double Offset,
    double WorstError,
    int Samples,
    int DistinctRaw,
    bool Fitted,
    double? R2 = null,
    int? Bit = null,
    bool Inverted = false,
    long? FieldMask = null,
    IReadOnlyList<SignalState>? States = null)
{
    /// <summary>The mask for a switch read from one bit, or a multi-state field's bits, or null for a number.</summary>
    public long? Mask => Bit is { } bit ? 1L << bit : FieldMask;

    /// <summary>True for a multi-state signal: named states, read from a field of bits (ADR-0056).</summary>
    public bool IsStates => States is { Count: > 0 };

    /// <summary>
    /// A multi-state signal: the bits of one byte that tell its states apart, each state the value
    /// those bits take (masked, not shifted — what <see cref="DecodeSpec"/> reads).
    /// </summary>
    public static ScalingCandidate ForStates(int byteOffset, long mask, IReadOnlyList<SignalState> states, int samples) =>
        new(new RawWindow(byteOffset, 1, false), 1, 0, 0, samples, states.Count, false, null, null, false, mask, states);

    /// <summary>The state a value names, or null.</summary>
    public string? StateName(double value) => SignalState.NameOf(States, value);

    /// <summary>A switch: one bit of one byte, read 1 for on.</summary>
    public static ScalingCandidate ForBit(int byteOffset, int bit, bool inverted, int samples, int statesSeen, double? agreement = null) =>
        new(new RawWindow(byteOffset, 1, false),
            inverted ? -1.0 / (1 << bit) : 1.0 / (1 << bit),
            inverted ? 1 : 0,
            0, samples, statesSeen, false, agreement, bit, inverted);

    /// <summary>The formula as people write it: <c>(A·256+B) × 0.0625</c>, <c>A − 40</c>.</summary>
    public string Formula
    {
        get
        {
            if (Bit is { } bit)
            {
                return $"{(Inverted ? "NOT " : "")}bit {bit} of {Window.Letters}  (on when {(Inverted ? 0 : 1)})";
            }

            if (IsStates && FieldMask is { } bitsMask)
            {
                var low = System.Numerics.BitOperations.TrailingZeroCount(bitsMask);
                var high = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)bitsMask);
                var bits = low == high ? $"bit {low}" : bitsMask == 0xFF ? "all" : $"bits {low}–{high}";
                return $"{bits} of {Window.Letters}: " + string.Join("  ", States!.Select(st => $"{st.Name}={(long)st.Value:X2}"));
            }

            var text = Window.Letters;
            if (Math.Abs(Scale - 1) > 1e-12)
            {
                text += Scale > 0 && Scale < 1 && Math.Abs((1 / Scale) - Math.Round(1 / Scale)) < 1e-9
                    ? $" ÷ {Fmt(1 / Scale)}"
                    : $" × {Fmt(Scale)}";
            }

            if (Math.Abs(Offset) > 1e-12)
            {
                text += Offset < 0 ? $" − {Fmt(-Offset)}" : $" + {Fmt(Offset)}";
            }

            return text;
        }
    }

    /// <summary>What it reads for an answer.</summary>
    public double? Apply(ReadOnlySpan<byte> payload) => Window.Read(payload) is { } raw
        ? ((Mask is { } mask ? raw & mask : raw) * Scale) + Offset
        : null;

    /// <summary>The catalog's decode for it.</summary>
    public DecodeSpec ToDecode(string unit) =>
        new(Window.Offset, Window.Length, Window.Signed, Scale, Offset, unit, Mask);

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// Finds scalings that turn an identifier's raw bytes into the numbers FORScan showed (ADR-0050).
/// </summary>
/// <remarks>
/// <b>Two ways, because one typed number proves little.</b> One sample fits dozens of byte and
/// scale combinations by chance, so the usual Ford scalings are tried first — a step of 1, ½, ¼,
/// ⅛, 1/16, 1/10, 1/100… with an offset of 0, −40 and a few others — and listed with how much
/// evidence each has. As the value moves and more samples arrive, coincidences fall away. With two
/// or more different raw numbers a straight line is also fitted, and kept when every sample lies on
/// it; its scale and offset snap to a usual one when they are within a hair of it.
/// </remarks>
public static class ScalingFitter
{
    /// <summary>The usual steps: binary fractions, decimals, and the percent of a byte.</summary>
    public static readonly double[] Scales =
    [
        1, 0.5, 0.25, 0.125, 0.0625, 0.03125, 0.015625, 0.0078125, 0.00390625,
        0.1, 0.01, 0.001, 0.2, 0.05, 0.02, 2, 4, 10,
        100.0 / 255, 1.0 / 255,
    ];

    /// <summary>The usual offsets: none, temperature's −40, and a few others Ford and SAE use.</summary>
    public static readonly double[] Offsets = [0, -40, -50, -64, -100, -128, -273.15, -1000];

    /// <summary>Candidates for typed samples, best first. Only ones every sample agrees with.</summary>
    public static IReadOnlyList<ScalingCandidate> FromSamples(IReadOnlyList<Sample> samples, int limit = 25)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        var length = samples.Min(s => s.Payload.Length);
        var found = new List<ScalingCandidate>();

        foreach (var window in RawWindow.For(length))
        {
            var raws = samples.Select(s => window.Read(s.Payload)!.Value).ToArray();
            var distinct = raws.Distinct().Count();

            foreach (var scale in Scales)
            {
                foreach (var offset in Offsets)
                {
                    if (Worst(raws, samples, scale, offset) is { } worst)
                    {
                        found.Add(new ScalingCandidate(window, scale, offset, worst, samples.Count, distinct, false));
                    }
                }
            }

            if (distinct >= 2 && Line(raws, samples.Select(s => s.Value).ToArray()) is { } line)
            {
                var (scale, offset) = Snap(line.Scale, line.Offset, raws, samples);
                if (Worst(raws, samples, scale, offset) is { } worst &&
                    !found.Any(c => c.Window == window && Math.Abs(c.Scale - scale) < 1e-9 && Math.Abs(c.Offset - offset) < 1e-6))
                {
                    found.Add(new ScalingCandidate(window, scale, offset, worst, samples.Count, distinct, true));
                }
            }
        }

        return [.. Rank(found).Take(limit)];
    }

    /// <summary>
    /// Fit a series — FORScan's logged column against the identifier's raw numbers at the same
    /// moments — and return the best line per window, best first.
    /// </summary>
    public static IReadOnlyList<ScalingCandidate> FromSeries(IReadOnlyList<(byte[] Payload, double Value)> points, int limit = 5)
    {
        if (points.Count < 3)
        {
            return [];
        }

        var length = points.Min(p => p.Payload.Length);
        var ys = points.Select(p => p.Value).ToArray();
        var found = new List<ScalingCandidate>();

        foreach (var window in RawWindow.For(length))
        {
            var xs = points.Select(p => (double)window.Read(p.Payload)!.Value).ToArray();
            var distinct = xs.Distinct().Count();
            if (distinct < 3 || LineFit(xs, ys) is not { } fit)
            {
                continue;
            }

            // Snap to a usual scale and offset only when that costs almost nothing: within 1% of the
            // range the value covered, worst point to worst point.
            var range = ys.Max() - ys.Min();
            var fittedWorst = Worst(xs, ys, fit.Scale, fit.Offset);
            var scale = SnapScale(fit.Scale);
            var offset = SnapOffset(fit.Offset + ((fit.Scale - scale) * xs.Average()), Math.Max(range * 0.01, Math.Abs(scale)));
            var worst = Worst(xs, ys, scale, offset);

            if (worst > fittedWorst + (range * 0.01))
            {
                (scale, offset, worst) = (fit.Scale, Math.Round(fit.Offset, 4), fittedWorst);
            }

            found.Add(new ScalingCandidate(window, scale, offset, worst, points.Count, distinct, true, fit.R2));
        }

        return [.. found
            .OrderByDescending(c => Math.Round(c.R2 ?? 0, 4))
            .ThenBy(c => c.Window.Signed)
            .ThenBy(c => c.Window.Length)
            .ThenBy(c => c.Window.Offset)
            .Take(limit)];
    }

    /// <summary>
    /// A scaling fitted against values in <paramref name="unit"/> — a °F log column — carried into the
    /// catalog's metric unit, and snapped to a usual step and offset when it lands within a hair of one.
    /// </summary>
    public static ScalingCandidate ToMetric(ScalingCandidate fitted, ShownUnit unit)
    {
        var (_, factor, _) = UnitConversion.ToMetric(0, unit);
        if (Math.Abs(factor - 1) < 1e-12 && Math.Abs(UnitConversion.ToMetric(0, unit).Value) < 1e-12)
        {
            return fitted;
        }

        var scale = SnapScale(fitted.Scale * factor);
        var offset = SnapOffset(UnitConversion.ToMetric(fitted.Offset, unit).Value, Math.Max(Math.Abs(scale), 0.05));
        return fitted with { Scale = scale, Offset = offset, WorstError = fitted.WorstError * factor };
    }

    /// <summary>
    /// Bits that agree with every switch state FORScan showed: 1 each time it said on and 0 each time
    /// off, or the other way round. Best first — ones that have seen both states, then plain before
    /// inverted, then lower bytes.
    /// </summary>
    /// <remarks>
    /// A switch is almost never a whole byte: a door ajar is one bit among the body module's flags, so
    /// the byte reads 08 for open and 00 for shut, and no step and offset turns that into 1 and 0.
    /// One state proves little — every bit that happens to match fits — so the candidates thin out as
    /// the switch is flipped and both states are typed.
    /// </remarks>
    public static IReadOnlyList<ScalingCandidate> FromStates(IReadOnlyList<(byte[] Payload, bool On)> samples, int limit = 25)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        var length = samples.Min(s => s.Payload.Length);
        var states = samples.Select(s => s.On).Distinct().Count();
        var found = new List<ScalingCandidate>();

        for (var i = 0; i < length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var plain = samples.All(s => ((s.Payload[i] >> bit) & 1) == (s.On ? 1 : 0));
                var inverted = samples.All(s => ((s.Payload[i] >> bit) & 1) == (s.On ? 0 : 1));

                if (plain)
                {
                    found.Add(ScalingCandidate.ForBit(i, bit, false, samples.Count, states));
                }

                if (inverted)
                {
                    found.Add(ScalingCandidate.ForBit(i, bit, true, samples.Count, states));
                }
            }
        }

        return [.. found.OrderBy(c => c.Inverted).ThenBy(c => c.Window.Offset).ThenBy(c => c.Bit).Take(limit)];
    }

    /// <summary>
    /// The bits that tell a multi-state signal's states apart (ADR-0056): for each byte, every run of
    /// adjacent bits whose value is the same each time a state was typed and different between states.
    /// Each byte keeps only its narrowest such fields — a wider one that merely contains a working field
    /// adds bits that never moved. Best first: narrowest, then lowest byte, then lowest bit.
    /// </summary>
    /// <remarks>
    /// The person switches through the states in the truck and types each as FORScan shows it — 2H,
    /// 4A, 4H, 4L — then goes round again. One pass proves little: any byte that happened to differ
    /// fits. A second pass drops whatever moves on its own (a counter, a checksum), because a state
    /// must read the same every time it is typed.
    /// </remarks>
    public static IReadOnlyList<ScalingCandidate> FromNamedStates(IReadOnlyList<(byte[] Payload, string State)> samples, int limit = 25)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        // One spelling per state, the first typed; case and spaces do not make another state.
        var names = new List<string>();
        var index = new List<int>();
        foreach (var (_, state) in samples)
        {
            var name = state.Trim();
            var at = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                names.Add(name);
                at = names.Count - 1;
            }

            index.Add(at);
        }

        var length = samples.Min(s => s.Payload.Length);
        var needed = Math.Max(1, (int)Math.Ceiling(Math.Log2(Math.Max(2, names.Count))));
        var found = new List<(ScalingCandidate Candidate, int Byte, int Low, int Width)>();

        for (var i = 0; i < length; i++)
        {
            var fields = new List<(int Low, int Width, long[] Values)>();

            for (var width = needed; width <= 8; width++)
            {
                for (var low = 0; low + width <= 8; low++)
                {
                    var mask = ((1L << width) - 1) << low;
                    var values = new long[names.Count];
                    var seen = new bool[names.Count];
                    var consistent = true;

                    for (var k = 0; k < samples.Count && consistent; k++)
                    {
                        var v = samples[k].Payload[i] & mask;
                        if (!seen[index[k]])
                        {
                            seen[index[k]] = true;
                            values[index[k]] = v;
                        }
                        else if (values[index[k]] != v)
                        {
                            consistent = false;
                        }
                    }

                    if (!consistent || values.Distinct().Count() != names.Count)
                    {
                        continue;
                    }

                    // Narrowest only: a field holding a working field inside it adds nothing.
                    if (fields.Any(f => f.Low >= low && f.Low + f.Width <= low + width))
                    {
                        continue;
                    }

                    fields.Add((low, width, values));
                }
            }

            foreach (var (low, width, values) in fields)
            {
                var states = names.Select((n, k) => new SignalState(values[k], n)).ToList();
                found.Add((ScalingCandidate.ForStates(i, ((1L << width) - 1) << low, states, samples.Count), i, low, width));
            }
        }

        return [.. found.OrderBy(f => f.Width).ThenBy(f => f.Byte).ThenBy(f => f.Low).Select(f => f.Candidate).Take(limit)];
    }

    /// <summary>
    /// For a logged column that takes two values — a switch — the bits that follow it, scored by how
    /// often they agree (carried in <see cref="ScalingCandidate.R2"/>), best first. At least 97%.
    /// </summary>
    public static IReadOnlyList<ScalingCandidate> FromBinarySeries(IReadOnlyList<(byte[] Payload, double Value)> points, int limit = 5)
    {
        var values = points.Select(p => p.Value).Distinct().OrderBy(v => v).ToList();
        if (values.Count != 2 || points.Count < 3)
        {
            return [];
        }

        var high = values[1];
        var length = points.Min(p => p.Payload.Length);
        var found = new List<ScalingCandidate>();

        for (var i = 0; i < length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var agree = points.Count(p => ((p.Payload[i] >> bit) & 1) == (p.Value == high ? 1 : 0));
                var fraction = (double)agree / points.Count;
                var inverted = fraction < 0.5;
                var score = inverted ? 1 - fraction : fraction;

                // A bit that never moved agrees with whichever state the log spent most time in;
                // it has to have taken both values to be evidence.
                var bitValues = points.Select(p => (p.Payload[i] >> bit) & 1).Distinct().Count();
                if (score >= 0.97 && bitValues == 2)
                {
                    found.Add(ScalingCandidate.ForBit(i, bit, inverted, points.Count, 2, score));
                }
            }
        }

        return [.. found.OrderByDescending(c => c.R2).ThenBy(c => c.Inverted).ThenBy(c => c.Window.Offset).ThenBy(c => c.Bit).Take(limit)];
    }

    private static IEnumerable<ScalingCandidate> Rank(List<ScalingCandidate> found) => found
        .OrderByDescending(c => c.DistinctRaw)
        .ThenBy(c => c.Fitted)
        .ThenBy(c => Simplicity(c))
        .ThenBy(c => c.WorstError);

    /// <summary>Lower is plainer: unsigned before signed, a usual offset before an odd one, one byte before two.</summary>
    private static int Simplicity(ScalingCandidate c) =>
        (c.Window.Signed ? 4 : 0)
        + (c.Offset is 0 or -40 ? 0 : 2)
        + Array.IndexOf(Scales, c.Scale) switch { < 0 => 3, < 9 => 0, _ => 1 }
        + c.Window.Offset;

    private static double? Worst(long[] raws, IReadOnlyList<Sample> samples, double scale, double offset)
    {
        var worst = 0.0;

        for (var i = 0; i < raws.Length; i++)
        {
            var error = Math.Abs((raws[i] * scale) + offset - samples[i].Value);
            if (error > samples[i].Tolerance)
            {
                return null;
            }

            worst = Math.Max(worst, error);
        }

        return worst;
    }

    private static (double Scale, double Offset)? Line(long[] raws, double[] values) =>
        LineFit(raws.Select(r => (double)r).ToArray(), values) is { } fit ? (fit.Scale, fit.Offset) : null;

    private static (double Scale, double Offset, double R2)? LineFit(double[] xs, double[] ys)
    {
        var n = xs.Length;
        var mx = xs.Average();
        var my = ys.Average();
        double sxx = 0, sxy = 0, syy = 0;

        for (var i = 0; i < n; i++)
        {
            sxx += (xs[i] - mx) * (xs[i] - mx);
            sxy += (xs[i] - mx) * (ys[i] - my);
            syy += (ys[i] - my) * (ys[i] - my);
        }

        if (sxx <= 0)
        {
            return null;
        }

        var scale = sxy / sxx;
        var offset = my - (scale * mx);
        var r2 = syy <= 0 ? 0 : (sxy * sxy) / (sxx * syy);
        return (scale, offset, r2);
    }

    private static double Worst(double[] xs, double[] ys, double scale, double offset) =>
        xs.Zip(ys, (x, y) => Math.Abs((x * scale) + offset - y)).Max();

    private static (double Scale, double Offset) Snap(double scale, double offset, long[] raws, IReadOnlyList<Sample> samples)
    {
        var snappedScale = SnapScale(scale);
        var mean = raws.Average();
        var snappedOffset = SnapOffset(offset + ((scale - snappedScale) * mean), samples.Min(s => s.Tolerance) * 2);

        // Keep the snapped line only if every sample still agrees; otherwise the fitted one.
        return Worst(raws, samples, snappedScale, snappedOffset) is not null
            ? (snappedScale, snappedOffset)
            : (scale, offset);
    }

    private static double SnapScale(double scale)
    {
        foreach (var usual in Scales)
        {
            if (Math.Abs(scale - usual) <= Math.Abs(usual) * 0.01)
            {
                return usual;
            }
        }

        return scale;
    }

    private static double SnapOffset(double offset, double tolerance)
    {
        foreach (var usual in Offsets)
        {
            if (Math.Abs(offset - usual) <= tolerance)
            {
                return usual;
            }
        }

        return Math.Round(offset, 4);
    }
}
