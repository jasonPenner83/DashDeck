using System.Globalization;

namespace DashDeck.Vehicle.Elm;

/// <summary>
/// Turns raw ELM327/STN text into payload bytes.
/// </summary>
/// <remarks>
/// This parser is the reason the synthetic vehicle speaks ELM ASCII at the transport
/// level instead of handing over decoded values: a simulator that bypassed this code
/// would leave it untested until the first drive.
/// <para>
/// Real adapter output is messier than the documentation suggests — echoed commands,
/// <c>SEARCHING...</c> lines, multi-line ISO-TP responses with frame indices, spacing
/// that varies with <c>ATS0</c>/<c>ATS1</c>, and CAN headers when <c>ATH1</c> is on.
/// Everything here is defensive on purpose.
/// </para>
/// </remarks>
public static class ElmResponseParser
{
    private static readonly string[] NoDataMarkers =
        ["NO DATA", "UNABLE TO CONNECT", "STOPPED", "?"];

    private static readonly string[] BusErrorMarkers =
        ["BUS ERROR", "BUS INIT", "CAN ERROR", "DATA ERROR", "BUFFER FULL", "FB ERROR", "LV RESET"];

    /// <summary>
    /// Parse a response to a mode/PID request, returning only the payload bytes that
    /// follow the echoed mode and PID.
    /// </summary>
    public static PidResponse Parse(PidRequest request, string raw, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PidResponse.Failed(request, PidFailure.Timeout, at);
        }

        var upper = raw.ToUpperInvariant();

        if (BusErrorMarkers.Any(m => upper.Contains(m, StringComparison.Ordinal)))
        {
            return PidResponse.Failed(request, PidFailure.BusError, at);
        }

        if (NoDataMarkers.Any(m => upper.Contains(m, StringComparison.Ordinal)))
        {
            return PidResponse.Failed(request, PidFailure.NoData, at);
        }

        var bytes = ExtractBytes(upper, request);
        if (bytes is null)
        {
            return PidResponse.Failed(request, PidFailure.Malformed, at);
        }

        // A positive response echoes mode + 0x40, then the PID, then the payload.
        var expectedMode = (byte)(request.Mode + 0x40);
        var pidLength = request.Pid <= 0xFF ? 1 : 2;
        var headerLength = 1 + pidLength;

        if (bytes.Count < headerLength || bytes[0] != expectedMode)
        {
            return PidResponse.Failed(request, PidFailure.Malformed, at);
        }

        for (var i = 0; i < pidLength; i++)
        {
            var expected = (byte)(request.Pid >> ((pidLength - 1 - i) * 8));
            if (bytes[i + 1] != expected)
            {
                return PidResponse.Failed(request, PidFailure.Malformed, at);
            }
        }

        var payload = bytes.Skip(headerLength).ToArray();
        return payload.Length == 0
            ? PidResponse.Failed(request, PidFailure.Malformed, at)
            : PidResponse.Ok(request, payload, at);
    }

    /// <summary>
    /// Pull hex byte pairs out of adapter output, discarding echo, prompts, status lines,
    /// ISO-TP frame indices and CAN headers.
    /// </summary>
    private static List<byte>? ExtractBytes(string upper, PidRequest request)
    {
        var echo = request.ToCommand();
        var result = new List<byte>();

        foreach (var rawLine in upper.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace(">", string.Empty, StringComparison.Ordinal).Trim();

            if (line.Length == 0 ||
                line.Equals(echo, StringComparison.Ordinal) ||
                line.StartsWith("SEARCHING", StringComparison.Ordinal) ||
                line.Equals("OK", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = Tokenize(line);
            if (tokens is null)
            {
                // A line we cannot read at all is a parse failure, not something to skip:
                // silently ignoring it would turn corrupt data into plausible data.
                return null;
            }

            // Frame indices and CAN headers are stripped in Tokenize, which is the single
            // place that decides what is payload and what is framing. Doing it again here
            // would eat the first real byte.
            result.AddRange(tokens);
        }

        return result;
    }

    /// <summary>
    /// Split a line into payload bytes, tolerating both spaced and unspaced hex and
    /// discarding framing.
    /// </summary>
    /// <remarks>
    /// The single place that decides what is payload and what is framing. Two things get
    /// dropped: ISO-TP frame indices (<c>0:</c>, <c>1:</c>) on multi-line responses, and an
    /// 11-bit CAN header (three hex digits, e.g. <c>7E8</c>) when <c>ATH1</c> is on. Both
    /// only ever appear at the start of a line.
    /// </remarks>
    private static List<byte>? Tokenize(string line)
    {
        var bytes = new List<byte>();
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var text = token.TrimEnd(':');

            if (text.Length == 0)
            {
                continue;
            }

            // An ISO-TP frame index such as "0:" leaves a single digit behind.
            if (text.Length == 1 && token.EndsWith(':'))
            {
                continue;
            }

            // A leading 11-bit CAN header, present only when headers are enabled.
            if (index == 0 && text.Length == 3 && IsHex(text))
            {
                continue;
            }

            if (text.Length % 2 != 0)
            {
                return null;
            }

            for (var i = 0; i < text.Length; i += 2)
            {
                if (!byte.TryParse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    return null;
                }

                bytes.Add(b);
            }
        }

        return bytes;
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
