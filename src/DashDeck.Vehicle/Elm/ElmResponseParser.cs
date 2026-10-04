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

        // A negative response: 7F, the mode asked, and why not. The module is there — which is
        // all a module sweep needs to know — and has declined this one request (ADR-0035).
        if (bytes.Count >= 3 && bytes[0] == NegativeResponse && bytes[1] == request.Mode)
        {
            return PidResponse.Refused(request, bytes[2], at);
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

    private const byte NegativeResponse = 0x7F;

    /// <summary>
    /// True when the only thing in <paramref name="raw"/> is "busy — the real answer follows"
    /// (<c>7F xx 78</c>): the module has not answered yet, and the adapter stopped listening.
    /// </summary>
    public static bool IsOnlyPending(string raw)
    {
        var any = false;

        foreach (var rawLine in raw.ToUpperInvariant().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace(">", string.Empty, StringComparison.Ordinal).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = Tokenize(line, out _);
            if (tokens is not [NegativeResponse, _, ResponsePending])
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>The negative response code that means "busy — the real answer follows".</summary>
    private const byte ResponsePending = 0x78;

    /// <summary>
    /// Pull hex byte pairs out of adapter output, discarding echo, prompts, status lines,
    /// ISO-TP frame indices and CAN headers.
    /// </summary>
    /// <remarks>
    /// Two cases join lines differently. Lines carrying ISO-TP frame indices are one reply
    /// split across frames, and are concatenated. Lines without are each a whole single-frame
    /// reply, and more than one means more than one module answered the broadcast — the PCM
    /// and the TCM both answer mode 01 PID 00 on many trucks. Concatenating those used to read
    /// as one long malformed reply; the first is taken instead. Asking one module by address
    /// (ADR-0035) is how to hear a particular one.
    /// </remarks>
    private static List<byte>? ExtractBytes(string upper, PidRequest request)
    {
        var echo = request.ToCommand();
        var result = new List<byte>();
        var replies = 0;
        var framed = false;

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

            var tokens = Tokenize(line, out var isFrame);
            if (tokens is null)
            {
                // A line we cannot read at all is a parse failure, not something to skip:
                // silently ignoring it would turn corrupt data into plausible data.
                return null;
            }

            // "Response pending": the module is busy and the real answer is the next line.
            if (tokens is [NegativeResponse, _, ResponsePending])
            {
                continue;
            }

            if (tokens.Count == 0)
            {
                // A multi-frame byte count ("014") is all framing.
                continue;
            }

            framed |= isFrame;

            if (!framed && replies > 0)
            {
                // A second complete reply from another module. Keep the first.
                continue;
            }

            replies++;

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
    /// dropped: ISO-TP frame indices (<c>0:</c>, <c>1:</c>, spaced or glued to the data) on
    /// multi-line responses, and an 11-bit CAN header (three hex digits, e.g. <c>7E8</c>) when
    /// <c>ATH1</c> is on. Both only ever appear at the start of a line.
    /// </remarks>
    private static List<byte>? Tokenize(string line, out bool isFrame)
    {
        isFrame = false;
        var bytes = new List<byte>();
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];

            // With spaces off (ATS0, which ElmAdapter sets) a frame index is glued to its data:
            // "0:490201314654". Nothing used it until the VIN, the first multi-frame reply, and
            // the whole line read as unparseable (ADR-0033). Peel the index off and carry on.
            if (index == 0 && token.Length > 2 && token[1] == ':' && Uri.IsHexDigit(token[0]))
            {
                token = token[2..];
                isFrame = true;
            }

            var text = token.TrimEnd(':');

            if (text.Length == 0)
            {
                continue;
            }

            // An ISO-TP frame index such as "0:" leaves a single digit behind.
            if (text.Length == 1 && token.EndsWith(':'))
            {
                isFrame = true;
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
