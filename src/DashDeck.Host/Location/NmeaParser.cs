using System.Globalization;
using DashDeck.Abstractions;

namespace DashDeck.Host.Location;

/// <summary>
/// Turns an NMEA 0183 sentence into a <see cref="LocationFix"/>.
/// </summary>
/// <remarks>
/// The format every phone GPS-share app speaks. Deliberately pure and side-effect free — it takes
/// a line and the time it arrived and returns a fix — because the parsing is the part with edge
/// cases (talker ids, hemispheres, an empty course when stopped, a bad checksum) and so it is the
/// part that gets a truth table (F7 keeps the shell itself out of the tests, but this is not the
/// shell). Understands <c>RMC</c> (position, speed and course) and <c>GGA</c> (position and fix
/// quality); any talker id (GP, GN, GL…) is accepted.
/// </remarks>
public static class NmeaParser
{
    private const double KnotsToKmh = 1.852;

    /// <summary>
    /// Parse one sentence. Returns false for anything that is not a well-formed RMC or GGA — a
    /// bad checksum, a different sentence, junk. A valid sentence that reports <em>no</em> fix
    /// still parses (true) with <see cref="SignalQuality.Unavailable"/>, so the reader learns the
    /// fix was lost rather than holding the last one forever.
    /// </summary>
    public static bool TryParse(string sentence, DateTimeOffset takenUtc, out LocationFix fix)
    {
        fix = default;

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return false;
        }

        var s = sentence.Trim();

        if (s.Length < 6 || s[0] != '$')
        {
            return false;
        }

        var star = s.IndexOf('*');
        string body;

        if (star >= 0)
        {
            body = s[1..star];
            var given = s[(star + 1)..].Trim();

            if (given.Length >= 2 && !ChecksumOk(body, given))
            {
                return false;
            }
        }
        else
        {
            body = s[1..];
        }

        var f = body.Split(',');
        var type = f[0].Length >= 3 ? f[0][^3..] : f[0];

        return type switch
        {
            "RMC" => TryRmc(f, takenUtc, out fix),
            "GGA" => TryGga(f, takenUtc, out fix),
            _ => false,
        };
    }

    private static bool TryRmc(string[] f, DateTimeOffset takenUtc, out LocationFix fix)
    {
        fix = default;

        if (f.Length < 9)
        {
            return false;
        }

        var hasFix = string.Equals(f[2], "A", StringComparison.OrdinalIgnoreCase);
        var lat = ParseCoordinate(f[3], f[4]);
        var lon = ParseCoordinate(f[5], f[6]);
        var speed = ParseDouble(f[7]);
        var course = ParseDouble(f[8]);

        var quality = hasFix && !double.IsNaN(lat) && !double.IsNaN(lon)
            ? SignalQuality.Live
            : SignalQuality.Unavailable;

        fix = new LocationFix(
            lat,
            lon,
            double.IsNaN(speed) ? double.NaN : speed * KnotsToKmh,
            course,
            quality,
            takenUtc);

        return true;
    }

    private static bool TryGga(string[] f, DateTimeOffset takenUtc, out LocationFix fix)
    {
        fix = default;

        if (f.Length < 7)
        {
            return false;
        }

        var quality = ParseInt(f[6]);
        var hasFix = quality > 0;
        var lat = ParseCoordinate(f[2], f[3]);
        var lon = ParseCoordinate(f[4], f[5]);

        fix = new LocationFix(
            lat,
            lon,
            double.NaN,
            double.NaN,
            hasFix && !double.IsNaN(lat) && !double.IsNaN(lon) ? SignalQuality.Live : SignalQuality.Unavailable,
            takenUtc);

        return true;
    }

    /// <summary>NMEA packs degrees and minutes as <c>ddmm.mmmm</c>; unpack to decimal degrees.</summary>
    private static double ParseCoordinate(string value, string hemisphere)
    {
        var raw = ParseDouble(value);

        if (double.IsNaN(raw))
        {
            return double.NaN;
        }

        var degrees = Math.Floor(raw / 100);
        var minutes = raw - (degrees * 100);
        var result = degrees + (minutes / 60);

        return hemisphere is "S" or "s" or "W" or "w" ? -result : result;
    }

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    private static bool ChecksumOk(string body, string given)
    {
        byte sum = 0;

        foreach (var c in body)
        {
            sum ^= (byte)c;
        }

        return int.TryParse(given.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected)
            && sum == expected;
    }
}
