using System.Text;
using DashDeck.Abstractions;
using DashDeck.Vehicle;

namespace DashDeck.Core.Identity;

/// <summary>
/// Vehicle identification numbers: tidying one up, checking it, and reading one off the truck.
/// </summary>
/// <remarks>
/// A VIN is personal data in the same sense a licence plate is. It is kept on the tablet, sent
/// only to the decoder a person asks to use, never logged, and never handed to components —
/// they get the decoded facts on <see cref="VehicleProfile"/>, not the number (ADR-0033).
/// </remarks>
public static class Vin
{
    /// <summary>Every VIN is seventeen characters, from 1981 on.</summary>
    public const int Length = 17;

    /// <summary>Upper-case, with the spaces and dashes people type or paste removed.</summary>
    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();

        foreach (var c in text ?? "")
        {
            if (!char.IsWhiteSpace(c) && c != '-')
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Seventeen characters from the VIN alphabet — digits and capitals without I, O and Q,
    /// which are left out because they read as 1 and 0.
    /// </summary>
    public static bool IsWellFormed(string? vin) =>
        vin is { Length: Length } && vin.All(IsVinCharacter);

    private static bool IsVinCharacter(char c) =>
        c is >= '0' and <= '9' || (c is >= 'A' and <= 'Z' && c is not ('I' or 'O' or 'Q'));

    /// <summary>
    /// The check digit position 9 should hold, under the North American rule (49 CFR 565).
    /// </summary>
    /// <remarks>
    /// Mandatory on vehicles built for North America and optional elsewhere, so a mismatch is
    /// a warning — most likely a typo — and never a refusal: DashDeck is meant to work for
    /// trucks it was not built in.
    /// </remarks>
    public static char? ExpectedCheckDigit(string vin)
    {
        if (!IsWellFormed(vin))
        {
            return null;
        }

        var sum = 0;

        for (var i = 0; i < Length; i++)
        {
            sum += Transliterate(vin[i]) * Weights[i];
        }

        var remainder = sum % 11;
        return remainder == 10 ? 'X' : (char)('0' + remainder);
    }

    /// <summary>True when position 9 matches <see cref="ExpectedCheckDigit"/>.</summary>
    public static bool HasValidCheckDigit(string vin) =>
        ExpectedCheckDigit(vin) is { } expected && vin[8] == expected;

    private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    private static int Transliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1,
        'B' or 'K' or 'S' => 2,
        'C' or 'L' or 'T' => 3,
        'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5,
        'F' or 'W' => 6,
        'G' or 'P' or 'X' => 7,
        'H' or 'Y' => 8,
        'R' or 'Z' => 9,
        _ => 0,
    };

    /// <summary>
    /// The VIN out of a mode 09 PID 02 payload, or null when there is not one in it.
    /// </summary>
    /// <remarks>
    /// The payload leads with a count of data items (01) and some ECUs pad the front with
    /// zeros, so this takes the last seventeen characters rather than assuming an offset.
    /// </remarks>
    public static string? FromMode09Payload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Length)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(payload[^Length..]);
        return IsWellFormed(text) ? text : null;
    }
}

/// <summary>What asking the truck for its VIN produced.</summary>
/// <param name="Vin">The VIN, or null.</param>
/// <param name="Problem">Why there is none, when there is none.</param>
public sealed record VinReadResult(string? Vin, string? Problem);

/// <summary>
/// Asks the vehicle for its own VIN: mode 09, PID 02.
/// </summary>
/// <remarks>
/// Every OBD-II vehicle from about 2005 answers it, and it is the truck-first answer
/// (ADR-0016) to "which vehicle is this" — no typing, no transcription errors. A read, so it
/// is inside the read-only rule (ADR-0006). Takes the request as a delegate, like the PID scan,
/// so it goes through <see cref="VehicleService.ProbeAsync"/> and never interleaves with polling.
/// </remarks>
public static class VinReader
{
    /// <summary>The request: mode 09, PID 02, on the powertrain bus.</summary>
    public static readonly PidRequest Request = new(0x09, 0x02, CanBus.Hs);

    /// <summary>Tries before giving up. A multi-frame reply is the likeliest to be dropped.</summary>
    public const int Attempts = 3;

    public static async Task<VinReadResult> ReadAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        PidResponse? response = null;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            response = await request(Request, ct).ConfigureAwait(false);

            if (response.IsSuccess)
            {
                return Vin.FromMode09Payload(response.Data) is { } vin
                    ? new VinReadResult(vin, null)
                    : new VinReadResult(null, "the truck answered, but not with a readable VIN");
            }
        }

        return new VinReadResult(null, response!.Failure switch
        {
            PidFailure.NoData => "the truck did not answer the VIN request (mode 09)",
            PidFailure.Timeout => "no reply — is the adapter connected?",
            PidFailure.BusError => "the adapter reported a bus error",
            _ => "a reply came back that could not be read",
        });
    }
}
