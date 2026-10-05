using System.Globalization;

namespace DashDeck.Core.Diagnostics;

/// <summary>Which list a trouble code came from.</summary>
public enum TroubleCodeKind
{
    /// <summary>Mode 03: confirmed, and what lights the check-engine light.</summary>
    Stored,

    /// <summary>Mode 07: seen on this or the last drive, not yet confirmed.</summary>
    Pending,
}

/// <summary>
/// One diagnostic trouble code, as SAE J2012 writes it: a letter for the system (P, C, B, U), a
/// digit for who defined it (0 and 2 the standard, 1 and 3 the maker), and three hex digits.
/// </summary>
/// <param name="Raw">The two bytes the module sent, big-endian.</param>
public readonly record struct TroubleCode(ushort Raw)
{
    private static readonly char[] Systems = ['P', 'C', 'B', 'U'];

    /// <summary><c>P</c> powertrain, <c>C</c> chassis, <c>B</c> body, <c>U</c> network.</summary>
    public char System => Systems[Raw >> 14];

    /// <summary>The code as written: <c>P0420</c>.</summary>
    public string Text => string.Create(
        CultureInfo.InvariantCulture,
        $"{System}{(Raw >> 12) & 0x3}{(Raw >> 8) & 0xF:X}{(Raw >> 4) & 0xF:X}{Raw & 0xF:X}");

    /// <summary>
    /// True for a code the standard defines (second character 0, or 2 for P); false for one the
    /// vehicle's maker defines, whose meaning only the maker's documents give.
    /// </summary>
    public bool IsGeneric => ((Raw >> 12) & 0x3) switch
    {
        0 => true,
        2 => System == 'P',
        _ => false,
    };

    public override string ToString() => Text;

    /// <summary>Read <c>P0420</c> (any case) back into a code.</summary>
    public static bool TryParse(string? text, out TroubleCode code)
    {
        code = default;
        var t = (text ?? "").Trim().ToUpperInvariant();
        if (t.Length != 5)
        {
            return false;
        }

        var system = Array.IndexOf(Systems, t[0]);
        if (system < 0 || t[1] is < '0' or > '3' ||
            !ushort.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rest))
        {
            return false;
        }

        code = new TroubleCode((ushort)((system << 14) | ((t[1] - '0') << 12) | rest));
        return true;
    }

    /// <summary>
    /// The codes in a mode 03 or 07 answer's payload (after the <c>43</c>/<c>47</c>). On CAN the
    /// first byte is how many follow; an older bus sends pairs alone, padded with zeros. Zero pairs
    /// are padding, never a code.
    /// </summary>
    public static IReadOnlyList<TroubleCode> FromPayload(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var start = 0;
        var count = int.MaxValue;

        // CAN: a count, then exactly that many pairs. An odd length says there is a count byte.
        if (payload.Length % 2 == 1)
        {
            count = payload[0];
            start = 1;
        }

        var codes = new List<TroubleCode>();
        for (var i = start; i + 1 < payload.Length && codes.Count < count; i += 2)
        {
            var raw = (ushort)((payload[i] << 8) | payload[i + 1]);
            if (raw != 0)
            {
                codes.Add(new TroubleCode(raw));
            }
        }

        return codes;
    }
}
