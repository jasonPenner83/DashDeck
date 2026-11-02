using System.Collections.Concurrent;
using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Calculated;

/// <summary>
/// What every calculation is told about the truck that the bus does not say (ADR-0081): the names
/// <c>displacement</c> and <c>correction</c> in a formula.
/// </summary>
/// <param name="DisplacementLitres">The engine's size, from the VIN decode. Zero when not known.</param>
/// <param name="Correction">The person's fuel estimate correction, from Settings ▸ Vehicle.</param>
public sealed record CalculationFacts(double DisplacementLitres, double Correction = 1.0)
{
    /// <summary>The smallest and largest correction the person can set.</summary>
    public const double MinimumCorrection = 0.5;

    /// <inheritdoc cref="MinimumCorrection"/>
    public const double MaximumCorrection = 1.5;

    /// <summary>Nothing known: a formula that needs the engine size reads Unavailable.</summary>
    public static CalculationFacts Unknown { get; } = new(0);

    /// <summary>The correction, kept inside its range.</summary>
    public double ClampedCorrection => double.IsFinite(Correction) ? Math.Clamp(Correction, MinimumCorrection, MaximumCorrection) : 1.0;
}

/// <summary>One input as it was when worked out: what it read, or that its default stood in.</summary>
/// <param name="Name">The formula's name for it.</param>
/// <param name="Signal">The signal.</param>
/// <param name="Value">The number used, or NaN when there was none.</param>
/// <param name="Unit">The reading's unit.</param>
/// <param name="Defaulted">True when the signal had no reading and the default was used.</param>
public readonly record struct WorkingInput(string Name, string Signal, double Value, string Unit, bool Defaulted);

/// <summary>A calculated signal worked out, with everything that went into it, for Settings ▸ Sensors.</summary>
/// <param name="Inputs">Each input, as read.</param>
/// <param name="Constants">The constants and facts the formula had.</param>
/// <param name="Formula">The formula used, after any case chose it; null when it could not be worked out.</param>
/// <param name="Result">What came out.</param>
public sealed record CalculationWorking(
    IReadOnlyList<WorkingInput> Inputs,
    IReadOnlyList<(string Name, double Value)> Constants,
    string? Formula,
    SignalValue Result)
{
    /// <summary>One line: each value going in, then what came out.</summary>
    public override string ToString()
    {
        var parts = Inputs.Select(i => double.IsNaN(i.Value)
                ? $"{i.Name} —"
                : Number($"{i.Name} {i.Value:0.###}{(i.Unit.Length > 0 ? " " + i.Unit : "")}{(i.Defaulted ? " (default)" : "")}"))
            .Concat(Constants.Select(c => Number($"{c.Name} {c.Value:0.###}")));

        var result = Result.Quality is SignalQuality.Unavailable || double.IsNaN(Result.Value)
            ? "no reading"
            : Number($"{Result.Value:0.##} {Result.Unit}").Trim();

        return $"{string.Join("  ·  ", parts)}  →  {result}";
    }

    private static string Number(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Signals the dash works out from other signals instead of asking the truck for (ADR-0081).
/// </summary>
/// <remarks>
/// <para>
/// <b>The formula is in the catalog.</b> A calculated entry's <c>calculate</c> block names its
/// inputs, its constants and its formula (<see cref="CalculationSpec"/>), so what the dash does
/// can be read in the file and in Settings ▸ Sensors, and changed in the person's own signal file
/// without a new build. This class only runs what the file says.
/// </para>
/// <para>
/// <b>Never a confident wrong number.</b> A required input with no reading, a fact the formula
/// needs and does not have, or an answer that is not a number reads Unavailable. The result is no
/// more trustworthy than the inputs it read: Stale over Simulated over Live.
/// </para>
/// <para>
/// An input that is itself calculated is worked out on the spot, so a chain reads the same moment
/// throughout.
/// </para>
/// </remarks>
public static class Calculations
{
    /// <summary>The unit fuel flow is in, and economy while stopped.</summary>
    public const string FlowUnit = "L/h";

    /// <summary>The unit economy is in while moving.</summary>
    public const string EconomyUnit = "L/100km";

    /// <summary>The engine's size in litres, 0 when not known.</summary>
    public const string Displacement = "displacement";

    /// <summary>The person's fuel estimate correction.</summary>
    public const string Correction = "correction";

    /// <summary>The names every formula is given, which an input or constant may not take.</summary>
    public static IReadOnlyList<string> Facts { get; } = [Displacement, Correction];

    /// <summary>How deep a chain of calculated inputs may go before it is taken to be a loop.</summary>
    private const int MaximumDepth = 8;

    private static readonly ConcurrentDictionary<string, Formula?> Parsed = new(StringComparer.Ordinal);

    /// <summary>
    /// What is wrong with a calculated definition as written, or nothing: a formula that will not
    /// read, a name it uses that is not given, a name given twice.
    /// </summary>
    public static IEnumerable<string> Check(SignalDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (d.Calculate is not { } spec)
        {
            yield break;
        }

        if (spec.Inputs.Count == 0)
        {
            yield return $"'{d.Id}': a calculation needs at least one input";
        }

        foreach (var (name, input) in spec.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Signal))
            {
                yield return $"'{d.Id}': input '{name}' names no signal";
            }

            if (input.Signal == d.Id)
            {
                yield return $"'{d.Id}': input '{name}' is the signal itself";
            }
        }

        var given = spec.Inputs.Keys.Concat(spec.Constants?.Keys ?? []).Concat(Facts).ToList();

        foreach (var twice in given.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"'{d.Id}': the name '{twice.Key}' is given twice (inputs, constants, and {string.Join(", ", Facts)} share one set of names)";
        }

        if ((spec.Formula is null) == (spec.Cases is not { Count: > 0 }))
        {
            yield return $"'{d.Id}': give a formula, or cases — one of the two";
        }

        if (spec.Cases is { Count: > 0 } cases && cases[^1].When is not null)
        {
            yield return $"'{d.Id}': the last case needs no \"when\", so there is always an answer";
        }

        foreach (var text in spec.Formulas)
        {
            if (Formula.TryParse(text, out var problem) is not { } formula)
            {
                yield return $"'{d.Id}': {problem}";
                continue;
            }

            foreach (var unknown in formula.Names.Where(n => !given.Contains(n, StringComparer.Ordinal)))
            {
                yield return $"'{d.Id}': '{unknown}' in \"{text}\" is not an input, a constant, or one of {string.Join(", ", Facts)}";
            }
        }
    }

    /// <summary>A calculated signal that reads itself, directly or through others; empty when there is none.</summary>
    public static IEnumerable<string> Loops(IReadOnlyCollection<SignalDefinition> definitions)
    {
        var byId = definitions.GroupBy(d => d.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var d in definitions.Where(d => d.IsCalculated))
        {
            if (Reaches(d.Id, d, byId, 0))
            {
                yield return $"'{d.Id}': its calculation reads itself through its inputs";
            }
        }

        static bool Reaches(string target, SignalDefinition from, Dictionary<string, SignalDefinition> byId, int depth) =>
            depth <= MaximumDepth
            && from.Calculate is { } spec
            && spec.Inputs.Values.Any(i => i.Signal == target
                || (byId.TryGetValue(i.Signal, out var next) && Reaches(target, next, byId, depth + 1)));
    }

    /// <summary>Work a calculated signal out from the latest readings.</summary>
    /// <param name="definition">The calculated signal.</param>
    /// <param name="find">Another signal's definition by id, for an input that is itself calculated.</param>
    /// <param name="read">The latest reading of a signal, as the bus has it, staleness included.</param>
    /// <param name="facts">The engine's size and the correction.</param>
    public static SignalValue Calculate(SignalDefinition definition, Func<string, SignalDefinition?> find, Func<string, SignalValue> read, CalculationFacts facts) =>
        Work(definition, find, read, facts, 0).Result;

    /// <summary>Work it out and keep everything that went in, for Settings ▸ Sensors.</summary>
    public static CalculationWorking Explain(SignalDefinition definition, Func<string, SignalDefinition?> find, Func<string, SignalValue> read, CalculationFacts facts) =>
        Work(definition, find, read, facts, 0);

    /// <summary>
    /// The unit to print beside a reading on a layout that wrote its own: the layout's, unless the
    /// reading has switched to the stopped unit of economy, which the layout cannot know about.
    /// </summary>
    public static string UnitShown(string layoutUnit, string? readingUnit) =>
        readingUnit == FlowUnit && layoutUnit == EconomyUnit ? FlowUnit : layoutUnit;

    private static CalculationWorking Work(SignalDefinition d, Func<string, SignalDefinition?> find, Func<string, SignalValue> read, CalculationFacts facts, int depth)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(facts);

        var missing = SignalValue.Missing(d.Id, d.Decode.Unit);

        if (d.Calculate is not { } spec || depth > MaximumDepth)
        {
            return new CalculationWorking([], [], null, missing);
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var inputs = new List<WorkingInput>();
        var qualities = new List<SignalQuality>();
        var newest = DateTimeOffset.MinValue;
        var complete = true;

        foreach (var (name, input) in spec.Inputs)
        {
            var reading = find(input.Signal) is { IsCalculated: true } chained
                ? Work(chained, find, read, facts, depth + 1).Result
                : read(input.Signal);

            if (HasNumber(reading))
            {
                values[name] = reading.Value;
                inputs.Add(new WorkingInput(name, input.Signal, reading.Value, reading.Unit, Defaulted: false));
                qualities.Add(reading.Quality);
                newest = reading.TimestampUtc > newest ? reading.TimestampUtc : newest;
            }
            else if (input.Default is { } fallback)
            {
                values[name] = fallback;
                inputs.Add(new WorkingInput(name, input.Signal, fallback, "", Defaulted: true));
            }
            else
            {
                inputs.Add(new WorkingInput(name, input.Signal, double.NaN, reading.Unit, Defaulted: false));
                complete = false;
            }
        }

        var constants = (spec.Constants ?? new Dictionary<string, double>()).Select(c => (c.Key, c.Value)).ToList();

        foreach (var (name, value) in constants)
        {
            values[name] = value;
        }

        // The facts, shown only when the formula uses them. An engine size of 0 is not known: a
        // formula that needs it has no answer rather than a zero.
        var used = spec.Formulas.Select(Read).Where(f => f is not null).SelectMany(f => f!.Names).ToHashSet(StringComparer.Ordinal);

        if (used.Contains(Displacement))
        {
            constants.Add((Displacement, facts.DisplacementLitres));
            complete &= facts.DisplacementLitres > 0 && double.IsFinite(facts.DisplacementLitres);
        }

        if (used.Contains(Correction))
        {
            constants.Add((Correction, facts.ClampedCorrection));
        }

        values[Displacement] = facts.DisplacementLitres;
        values[Correction] = facts.ClampedCorrection;

        if (!complete || qualities.Count == 0)
        {
            return new CalculationWorking(inputs, constants, null, missing);
        }

        double Value(string name) => values.TryGetValue(name, out var v) ? v : double.NaN;

        var (text, unit) = Choose(spec, Value);

        if (text is null || Read(text) is not { } formula)
        {
            return new CalculationWorking(inputs, constants, text, missing);
        }

        var result = formula.Evaluate(Value);
        unit ??= spec.Unit ?? d.Decode.Unit;

        var answer = double.IsFinite(result)
            ? new SignalValue(d.Id, result, unit, newest, qualities.Aggregate(SignalQuality.Live, Worse))
            : SignalValue.Missing(d.Id, unit);

        return new CalculationWorking(inputs, constants, text, answer);
    }

    /// <summary>The formula to use, and its unit when a case gives one.</summary>
    private static (string? Formula, string? Unit) Choose(CalculationSpec spec, Func<string, double> value)
    {
        if (spec.Formula is { } single)
        {
            return (single, null);
        }

        foreach (var c in spec.Cases ?? [])
        {
            if (c.When is null || Read(c.When) is { } when && when.Evaluate(value) is var held && !double.IsNaN(held) && held != 0)
            {
                return (c.Formula, c.Unit);
            }
        }

        return (null, null);
    }

    private static Formula? Read(string text) => Parsed.GetOrAdd(text, t => Formula.TryParse(t, out _));

    /// <summary>A number to work with: usable, or stale with one still in it.</summary>
    private static bool HasNumber(SignalValue v) =>
        double.IsFinite(v.Value) && v.Quality is SignalQuality.Live or SignalQuality.Simulated or SignalQuality.Stale;

    /// <summary>Stale beats Simulated beats Live: the least trustworthy input wins.</summary>
    private static SignalQuality Worse(SignalQuality a, SignalQuality b)
    {
        static int Rank(SignalQuality q) => q switch
        {
            SignalQuality.Unavailable => 3,
            SignalQuality.Stale => 2,
            SignalQuality.Simulated => 1,
            _ => 0,
        };

        return Rank(a) >= Rank(b) ? a : b;
    }
}
