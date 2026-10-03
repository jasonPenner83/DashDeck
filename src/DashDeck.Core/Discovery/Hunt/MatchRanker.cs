using System.Globalization;

namespace DashDeck.Core.Discovery.Hunt;

/// <summary>A scaling and a unit change that turn a raw value into what the cluster shows.</summary>
/// <param name="Scale">Multiplied first.</param>
/// <param name="Offset">Then added.</param>
/// <param name="Unit">Then converted: none, km→mi, L/100km→mpg, kPa→psi, °C→°F, and back.</param>
public sealed record MatchTransform(double Scale, double Offset, string Unit)
{
    public double Apply(long raw)
    {
        var value = (raw * Scale) + Offset;
        return Unit switch
        {
            "km→mi" => value * 0.621371,
            "mi→km" => value * 1.609344,
            "L/100km→mpg" => value > 0 ? 235.215 / value : double.NaN,
            "kPa→psi" => value * 0.1450377,
            "psi→kPa" => value * 6.894757,
            "°C→°F" => (value * 1.8) + 32,
            _ => value,
        };
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Scale != 1)
        {
            parts.Add(Scale < 1
                ? string.Create(CultureInfo.InvariantCulture, $"÷ {1 / Scale:0.##}")
                : string.Create(CultureInfo.InvariantCulture, $"× {Scale:0.##}"));
        }

        if (Offset != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{(Offset < 0 ? "−" : "+")} {Math.Abs(Offset):0.##}"));
        }

        if (Unit.Length > 0)
        {
            parts.Add(Unit);
        }

        return parts.Count == 0 ? "as is" : string.Join(' ', parts);
    }

    /// <summary>Every transform tried.</summary>
    public static IReadOnlyList<MatchTransform> All { get; } = Build();

    private static List<MatchTransform> Build()
    {
        double[] scales = [1, 0.1, 0.01, 0.5, 0.25, 0.125, 1.0 / 16, 2, 10];
        string[] units = ["", "km→mi", "mi→km", "L/100km→mpg", "kPa→psi", "psi→kPa", "°C→°F"];
        var all = new List<MatchTransform>();

        foreach (var unit in units)
        {
            foreach (var scale in scales)
            {
                all.Add(new MatchTransform(scale, 0, unit));
            }

            // Temperatures carry an offset.
            if (unit is "" or "°C→°F")
            {
                all.Add(new MatchTransform(1, -40, unit));
                all.Add(new MatchTransform(1.0 / 16, -40, unit));
                all.Add(new MatchTransform(0.1, -40, unit));
                all.Add(new MatchTransform(1, -50, unit));
            }
        }

        return all;
    }
}

/// <summary>An identifier that gives the number the cluster shows under some reading and transform.</summary>
public sealed record MatchHit(ushort Did, Reading Reading, MatchTransform Transform, long Raw, double Decoded)
{
    /// <summary>What ties two hits together across readings: the same bytes, read the same way.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Did:X4}|{Reading}|{Transform}");

    public string DidText => Did.ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>
/// Finds identifiers that hold a number the person read off the cluster — distance to empty,
/// average economy, a tyre pressure (ADR-0044).
/// </summary>
/// <remarks>
/// One reading matches far too much: some byte somewhere is 35. Two readings of the same thing at
/// different values, or one of each tyre, are what thin it out — see <see cref="MatchTally"/>.
/// </remarks>
public static class MatchRanker
{
    /// <param name="answers">Each identifier's latest answer.</param>
    /// <param name="shown">The number as the cluster shows it.</param>
    /// <param name="decimals">How many decimals the cluster shows — the tolerance is half the last digit, a little more.</param>
    public static IReadOnlyList<MatchHit> Find(IReadOnlyDictionary<ushort, byte[]> answers, double shown, int decimals)
    {
        var tolerance = 0.6 * Math.Pow(10, -decimals);
        var hits = new List<MatchHit>();

        foreach (var (did, data) in answers)
        {
            foreach (var reading in Reading.All(data.Length))
            {
                if (reading.Read(data) is not { } raw)
                {
                    continue;
                }

                foreach (var transform in MatchTransform.All)
                {
                    var decoded = transform.Apply(raw);
                    if (!double.IsNaN(decoded) && Math.Abs(decoded - shown) <= tolerance)
                    {
                        hits.Add(new MatchHit(did, reading, transform, raw, decoded));
                    }
                }
            }
        }

        return hits;
    }

    /// <summary>How many decimals a number was typed with: <c>35</c> → 0, <c>13.4</c> → 1.</summary>
    public static int Decimals(string typed)
    {
        var dot = typed.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : typed.Length - dot - 1;
    }
}

/// <summary>
/// Adds up match hits across readings. A hit seen for every reading of the same thing, and a
/// transform that also explains the other readings of the target (all four tyres), rise.
/// </summary>
public sealed class MatchTally
{
    private readonly Dictionary<string, Dictionary<string, MatchHit>> _byLabel = [];
    private readonly Dictionary<string, int> _rounds = [];
    private readonly Dictionary<(string Label, string Key), int> _count = [];

    /// <summary>Record the hits for one reading of <paramref name="label"/> (<c>FRONT LEFT</c>).</summary>
    public void Add(string label, IEnumerable<MatchHit> hits)
    {
        _rounds[label] = _rounds.GetValueOrDefault(label) + 1;
        var held = _byLabel.TryGetValue(label, out var h) ? h : _byLabel[label] = [];

        foreach (var hit in hits.DistinctBy(x => x.Key))
        {
            held[hit.Key] = hit;
            _count[(label, hit.Key)] = _count.GetValueOrDefault((label, hit.Key)) + 1;
        }
    }

    /// <summary>The labels recorded so far.</summary>
    public IEnumerable<string> Labels => _rounds.Keys;

    /// <summary>
    /// The hits for one label, best first, each with its score: the share of this label's readings
    /// it matched, plus a half for each other label whose readings the same reading and transform
    /// (on another identifier) explained every time.
    /// </summary>
    public IReadOnlyList<(MatchHit Hit, double Score, int Matched, int Rounds)> Ranked(string label)
    {
        if (!_byLabel.TryGetValue(label, out var hits))
        {
            return [];
        }

        var rounds = _rounds[label];

        return
        [
            .. hits.Values
                .Select(hit =>
                {
                    var matched = _count[(label, hit.Key)];
                    var shape = $"{hit.Reading}|{hit.Transform}";
                    var siblings = _byLabel
                        .Where(kv => kv.Key != label)
                        .Count(kv => kv.Value.Values.Any(other =>
                            other.Did != hit.Did &&
                            $"{other.Reading}|{other.Transform}" == shape &&
                            _count[(kv.Key, other.Key)] == _rounds[kv.Key]));
                    return (Hit: hit, Score: ((double)matched / rounds) + (0.5 * siblings), Matched: matched, Rounds: rounds);
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Hit.Transform.Scale == 1 && x.Hit.Transform.Offset == 0 && x.Hit.Transform.Unit.Length == 0 ? 0 : 1)
                .ThenByDescending(x => x.Hit.Reading.Length)
                .ThenBy(x => x.Hit.Did),
        ];
    }
}
