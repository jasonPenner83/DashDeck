namespace DashDeck.Core.Discovery.Matching;

/// <summary>A logged column, and the identifiers that move with it, best first.</summary>
public sealed record ColumnMatch(LogColumn Column, IReadOnlyList<(IdentifierKey Key, ScalingCandidate Scaling)> Candidates, string? Problem)
{
    public (IdentifierKey Key, ScalingCandidate Scaling)? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>The result of matching a log: the offset used to line it up, and each column's matches.</summary>
public sealed record LogMatchResult(TimeSpan Offset, DateTimeOffset LogStart, IReadOnlyList<ColumnMatch> Columns);

/// <summary>
/// Lines a PID log up against the traffic and matches every column to an identifier (ADR-0050).
/// </summary>
/// <remarks>
/// <b>Lining up is the hard part.</b> A log whose time column is the time of day lines up directly:
/// FORScan and the tap run on the same PC, on the same clock. One that counts from its own start
/// does not say when that start was. FORScan starts logging the values already on screen, so the
/// start is close to the moment their identifiers were first asked for together; each such moment
/// is tried, and the one under which the columns match best wins. An offset typed by hand overrides
/// the guess.
/// <para>
/// Each column is then matched by fitting a line against every identifier's bytes at the same
/// moments, scored by R². Only columns that moved can be matched — a constant fits everything.
/// </para>
/// </remarks>
public static class LogMatcher
{
    /// <summary>Points used per column while searching for the offset; all of them for the final fit.</summary>
    private const int SearchPoints = 120;

    public static LogMatchResult Match(ForscanLog log, IdentifierTable table, DateTimeOffset? manualStart = null)
    {
        var identifiers = table.Snapshot().Where(s => s.Count >= 3).ToList();
        if (identifiers.Count == 0 || log.Columns.Count == 0)
        {
            return new LogMatchResult(TimeSpan.Zero, manualStart ?? default, [.. log.Columns.Select(c => new ColumnMatch(c, [], "no traffic to match against"))]);
        }

        var first = identifiers.Min(s => s.FirstSeen);
        DateTimeOffset start;

        if (manualStart is { } typed)
        {
            start = typed;
        }
        else if (log.ClockTimes)
        {
            // A time of day: on the date the traffic was heard, in its offset from UTC.
            var local = first.ToLocalTime();
            start = new DateTimeOffset(local.Date, local.Offset);
        }
        else
        {
            var candidates = identifiers.Select(s => s.FirstSeen).Append(first).Distinct().OrderBy(t => t).ToList();
            start = candidates
                .Select(c => (Start: c, Score: Score(log, identifiers, c, SearchPoints)))
                .OrderByDescending(x => x.Score)
                .First().Start;
        }

        var columns = log.Columns.Select(c => MatchColumn(c, identifiers, start, int.MaxValue)).ToList();
        return new LogMatchResult(start - first, start, columns);
    }

    private static double Score(ForscanLog log, List<IdentifierStats> identifiers, DateTimeOffset start, int points) =>
        log.Columns
            .Where(c => c.DistinctValues >= 2)
            .Select(c => MatchColumn(c, identifiers, start, points).Best?.Scaling.R2 ?? 0)
            .DefaultIfEmpty(0)
            .Sum();

    private static ColumnMatch MatchColumn(LogColumn column, List<IdentifierStats> identifiers, DateTimeOffset start, int maxPoints)
    {
        var binary = column.DistinctValues == 2;
        if (column.DistinctValues < 2)
        {
            return new ColumnMatch(column, [], "it never changed — flip it or make it move while logging");
        }

        var step = Math.Max(1, column.Points.Count / maxPoints);
        var sampled = column.Points.Where((_, i) => i % step == 0).ToList();
        var found = new List<(IdentifierKey, ScalingCandidate)>();

        foreach (var stats in identifiers)
        {
            var pairs = new List<(byte[], double)>();

            foreach (var (time, value) in sampled)
            {
                if (stats.PayloadAt(start + time) is { } payload)
                {
                    pairs.Add((payload, value));
                }
            }

            // A column with two values is a switch: matched bit by bit, scored by agreement.
            var candidates = binary ? ScalingFitter.FromBinarySeries(pairs, limit: 1) : ScalingFitter.FromSeries(pairs, limit: 1);
            foreach (var candidate in candidates)
            {
                if (candidate.R2 >= (binary ? 0.97 : 0.5))
                {
                    found.Add((stats.Key, candidate));
                }
            }
        }

        var ranked = found.OrderByDescending(f => f.Item2.R2).Take(5).ToList();
        return new ColumnMatch(column, ranked, ranked.Count == 0 ? "nothing in the traffic moved with it" : null);
    }
}
