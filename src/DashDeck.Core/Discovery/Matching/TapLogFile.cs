using System.Globalization;
using DashDeck.Vehicle.Tap;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>
/// Reads a log the serial tap saved back into <see cref="TapLine"/>s, so a capture made yesterday
/// can be matched today (ADR-0050).
/// </summary>
public static class TapLogFile
{
    public static IEnumerable<TapLine> Read(string text)
    {
        DateTimeOffset? started = null;
        var previous = TimeSpan.Zero;
        var day = DateTimeOffset.MinValue;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimStart('﻿').TrimEnd('\r');

            if (line.StartsWith('#'))
            {
                var at = line.IndexOf("started ", StringComparison.Ordinal);
                if (at >= 0 && DateTimeOffset.TryParseExact(line[(at + 8)..].Trim(), "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s))
                {
                    started = s;
                    day = new DateTimeOffset(s.Date, s.Offset);
                    previous = s.TimeOfDay;
                }

                continue;
            }

            // "07:47:11.456  >>  221E1C1"
            if (line.Length < 18 || !TimeSpan.TryParseExact(line[..12], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time))
            {
                continue;
            }

            if (started is null)
            {
                day = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
                started = day;
            }

            if (time < previous - TimeSpan.FromHours(12))
            {
                day = day.AddDays(1);   // past midnight
            }

            previous = time;
            var arrow = line.Substring(14, 2);
            var body = line.Length > 18 ? line[18..] : "";
            var at2 = day + time;

            switch (arrow)
            {
                case ">>":
                    yield return new TapLine(at2, TapDirection.ToAdapter, body);
                    break;

                case "<<":
                    if (body == ">")
                    {
                        yield return new TapLine(at2, TapDirection.FromAdapter, "", Prompt: true);
                    }
                    else if (body.EndsWith(" >", StringComparison.Ordinal))
                    {
                        yield return new TapLine(at2, TapDirection.FromAdapter, body[..^2], Prompt: true);
                    }
                    else
                    {
                        yield return new TapLine(at2, TapDirection.FromAdapter, body);
                    }

                    break;

                default:
                    yield return new TapLine(at2, null, body);
                    break;
            }
        }
    }
}
