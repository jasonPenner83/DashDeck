using System.Globalization;

namespace DashDeck.Core.Discovery.Hunt;

/// <summary>One pass over the watched identifiers, with the standard reading it is compared to.</summary>
/// <param name="At">When the pass ended.</param>
/// <param name="Reference">The reference's value — coolant temperature, rpm — or null if it went unanswered.</param>
/// <param name="Answers">Each identifier's answer this pass; one that went unanswered is absent.</param>
public sealed record FollowPass(TimeSpan At, double? Reference, IReadOnlyDictionary<ushort, byte[]> Answers);

/// <summary>An identifier whose value moved with the reference.</summary>
/// <param name="Did">The identifier.</param>
/// <param name="Reading">Which bytes, how read.</param>
/// <param name="Correlation">Pearson's r, −1 to 1: how closely it moved with the reference.</param>
/// <param name="Slope">The straight line through it: reference ≈ slope × value + intercept.</param>
/// <param name="Intercept">See <paramref name="Slope"/>.</param>
/// <param name="Points">Passes that had both.</param>
/// <param name="Low">Its lowest raw value.</param>
/// <param name="High">Its highest raw value.</param>
public sealed record FollowCandidate(ushort Did, Reading Reading, double Correlation, double Slope, double Intercept, int Points, long Low, long High)
{
    /// <summary>A readable guess at its scaling, when the fitted line is close to a familiar one.</summary>
    public string? Hint => ScalingHints.Describe(Slope, Intercept);

    public string DidText => Did.ToString("X4", CultureInfo.InvariantCulture);
}

/// <summary>
/// Ranks identifiers by how closely they moved with a standard reading while the truck was made to
/// change it — warming (coolant), revving (rpm) (ADR-0044).
/// </summary>
/// <remarks>
/// It does not say the identifier <em>is</em> the reference: oil follows coolant up while warming
/// without being coolant. It says which few of hundreds are worth a TEST.
/// </remarks>
public static class FollowRanker
{
    /// <summary>Fewer passes than this and no correlation means anything.</summary>
    public const int MinimumPoints = 6;

    public static IReadOnlyList<FollowCandidate> Rank(IReadOnlyList<FollowPass> passes)
    {
        var best = new Dictionary<ushort, FollowCandidate>();
        var dids = passes.SelectMany(p => p.Answers.Keys).Distinct();

        foreach (var did in dids)
        {
            var length = passes.Select(p => p.Answers.TryGetValue(did, out var d) ? d.Length : 0).Max();

            foreach (var reading in Reading.All(length))
            {
                var xs = new List<double>();
                var ys = new List<double>();

                foreach (var pass in passes)
                {
                    if (pass.Reference is { } reference &&
                        pass.Answers.TryGetValue(did, out var data) &&
                        reading.Read(data) is { } raw)
                    {
                        xs.Add(raw);
                        ys.Add(reference);
                    }
                }

                if (Fit(xs, ys) is not { } fit)
                {
                    continue;
                }

                var candidate = new FollowCandidate(did, reading, fit.R, fit.Slope, fit.Intercept, xs.Count, (long)xs.Min(), (long)xs.Max());

                if (!best.TryGetValue(did, out var held) ||
                    Math.Abs(candidate.Correlation) > Math.Abs(held.Correlation) + 0.02 ||
                    (Math.Abs(candidate.Correlation) > Math.Abs(held.Correlation) - 0.02 && candidate.Reading.Length > held.Reading.Length))
                {
                    best[did] = candidate;
                }
            }
        }

        return [.. best.Values.OrderByDescending(c => Math.Abs(c.Correlation)).ThenBy(c => c.Did)];
    }

    internal static (double R, double Slope, double Intercept)? Fit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count < MinimumPoints)
        {
            return null;
        }

        var mx = xs.Average();
        var my = ys.Average();
        double sxx = 0, syy = 0, sxy = 0;

        for (var i = 0; i < xs.Count; i++)
        {
            var dx = xs[i] - mx;
            var dy = ys[i] - my;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        if (sxx <= 0 || syy <= 0)
        {
            // Either it never moved or the reference never did: nothing to compare.
            return null;
        }

        var slope = sxy / sxx;
        return (sxy / Math.Sqrt(sxx * syy), slope, my - (slope * mx));
    }
}

/// <summary>Names a fitted line that looks like a scaling Ford and SAE use.</summary>
public static class ScalingHints
{
    private static readonly (double Slope, double Intercept, string Text)[] Known =
    [
        (1, -40, "value − 40"),
        (1.0 / 16, -40, "value ÷ 16 − 40"),
        (1, 0, "value as is"),
        (0.1, 0, "value ÷ 10"),
        (0.01, 0, "value ÷ 100"),
        (0.25, 0, "value ÷ 4"),
        (1.0 / 16, 0, "value ÷ 16"),
        (0.1, -40, "value ÷ 10 − 40"),
    ];

    /// <summary>The closest familiar scaling, or null when none is close.</summary>
    public static string? Describe(double slope, double intercept)
    {
        foreach (var (s, i, text) in Known)
        {
            if (Math.Abs(slope - s) <= Math.Abs(s) * 0.15 && Math.Abs(intercept - i) <= 8)
            {
                return text;
            }
        }

        return null;
    }
}
