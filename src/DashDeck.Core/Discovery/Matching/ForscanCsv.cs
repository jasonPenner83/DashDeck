using System.Globalization;
using System.Text.RegularExpressions;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>One column of a logged value: its name, the unit in its heading if any, and its points.</summary>
/// <param name="Points">Time from the log's start (or the clock time, when <see cref="ForscanLog.ClockTimes"/>), and the value.</param>
public sealed record LogColumn(string Name, string? Unit, IReadOnlyList<(TimeSpan Time, double Value)> Points)
{
    /// <summary>How many different values it took. A column that never moved cannot be matched.</summary>
    public int DistinctValues => Points.Select(p => p.Value).Distinct().Count();
}

/// <summary>A PID log read from a CSV file.</summary>
/// <param name="ClockTimes">True when the time column is a time of day; false when it counts from the log's start.</param>
public sealed record ForscanLog(IReadOnlyList<LogColumn> Columns, bool ClockTimes, IReadOnlyList<string> Problems);

/// <summary>
/// Reads FORScan's PID log, and any CSV shaped like it: a heading row, a time column, a column per
/// value (ADR-0050).
/// </summary>
/// <remarks>
/// <b>Tolerant on purpose.</b> FORScan's export has changed between versions and depends on the
/// PC's regional settings, and no sample of the owner's is in hand yet. So the delimiter is whatever
/// the heading row uses most (comma, semicolon or tab), a decimal comma is accepted when the
/// delimiter is not a comma, the time column is the one whose heading says "time" (else the first),
/// and its values may be seconds, milliseconds or a time of day. Units in a heading's brackets are
/// kept: "TFT (°F)". Empty cells — a value not polled on that row — are skipped, not zero.
/// </remarks>
public static partial class ForscanCsv
{
    public static ForscanLog Parse(string text)
    {
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();

        if (lines.Count < 2)
        {
            return new ForscanLog([], false, ["the file has no rows"]);
        }

        var header = lines[0].TrimStart('﻿');
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => header.Count(c => c == d)).First();
        var decimalComma = delimiter != ',';
        var names = Split(header, delimiter);

        var timeIndex = names.FindIndex(n => n.Contains("time", StringComparison.OrdinalIgnoreCase));
        if (timeIndex < 0)
        {
            timeIndex = 0;
        }

        var rows = lines.Skip(1).Select(l => Split(l, delimiter)).ToList();
        var times = new List<(TimeSpan? Time, bool Clock)>();

        foreach (var row in rows)
        {
            times.Add(timeIndex < row.Count ? ParseTime(row[timeIndex], decimalComma) : (null, false));
        }

        // A time of day, or a stopwatch shown as hh:mm:ss? A log that starts in the first hour after
        // midnight is read as a stopwatch, which is right far more often than not.
        var colons = times.Count(t => t.Clock) > times.Count / 2;
        var firstTime = times.FirstOrDefault(t => t.Time is not null).Time;
        var clock = colons && firstTime is { } ft && ft >= TimeSpan.FromHours(1);
        var numericTimes = times.Where(t => !t.Clock && t.Time is not null).Select(t => t.Time!.Value.TotalSeconds).ToList();

        // Seconds or milliseconds? A log a few minutes long counted in milliseconds runs to six
        // digits, and its steps are tens to hundreds; in seconds, its steps are fractions.
        var milliseconds = !colons && (names[timeIndex].Contains("ms", StringComparison.OrdinalIgnoreCase)
            || (numericTimes.Count > 2 && MedianStep(numericTimes) >= 5));

        var columns = new List<LogColumn>();

        for (var c = 0; c < names.Count; c++)
        {
            if (c == timeIndex)
            {
                continue;
            }

            var points = new List<(TimeSpan, double)>();

            for (var r = 0; r < rows.Count; r++)
            {
                if (times[r].Time is not { } t || c >= rows[r].Count || !TryNumber(rows[r][c], decimalComma, out var v))
                {
                    continue;
                }

                points.Add((milliseconds ? TimeSpan.FromMilliseconds(t.TotalSeconds) : t, v));
            }

            if (points.Count == 0)
            {
                continue;
            }

            var (name, unit) = NameAndUnit(names[c]);
            columns.Add(new LogColumn(name, unit, points));
        }

        if (columns.Count == 0)
        {
            problems.Add("no column had numbers in it");
        }

        return new ForscanLog(columns, clock, problems);
    }

    private static List<string> Split(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == delimiter && !quoted)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(ch);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static (TimeSpan? Time, bool Clock) ParseTime(string cell, bool decimalComma)
    {
        var text = cell.Trim();

        if (text.Contains(':', StringComparison.Ordinal))
        {
            var normal = decimalComma ? text.Replace(',', '.') : text;
            return TimeSpan.TryParseExact(normal, [@"h\:mm\:ss\.FFFFFFF", @"h\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss", @"m\:ss\.FFFFFFF", @"mm\:ss\.FFFFFFF"], CultureInfo.InvariantCulture, out var span)
                ? (span, true)
                : (null, false);
        }

        return TryNumber(text, decimalComma, out var number) ? (TimeSpan.FromSeconds(number), false) : (null, false);
    }

    private static bool TryNumber(string cell, bool decimalComma, out double value)
    {
        var text = cell.Trim();
        if (decimalComma)
        {
            text = text.Replace(',', '.');
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double MedianStep(List<double> values)
    {
        var steps = values.Zip(values.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
        return steps.Count == 0 ? 0 : steps[steps.Count / 2];
    }

    private static (string Name, string? Unit) NameAndUnit(string heading)
    {
        var match = UnitInBrackets().Match(heading);
        return match.Success
            ? (heading[..match.Index].Trim(), match.Groups[1].Value.Trim())
            : (heading.Trim(), null);
    }

    [GeneratedRegex(@"[\(\[]([^\)\]]*)[\)\]]\s*$")]
    private static partial Regex UnitInBrackets();
}
