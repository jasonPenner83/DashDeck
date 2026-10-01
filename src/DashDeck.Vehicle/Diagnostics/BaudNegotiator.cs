using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Diagnostics;

/// <summary>What a baud-rate attempt looked like.</summary>
public sealed record BaudAttempt(int BaudRate, bool Succeeded, string Reply);

/// <summary>
/// Finds the baud rate the adapter is actually sitting at.
/// </summary>
/// <remarks>
/// The OBDLink EX ships at 115200 but its STN chip supports up to 2 Mbps, and other
/// software raises it — FORScan runs this adapter at 2,000,000 bps. Whether it reverts on
/// power-cycle depends on whether the change was written persistently, so the rate cannot
/// be assumed from the hardware model.
/// <para>
/// Opening at the wrong rate is worse than failing: the port opens happily and returns
/// mojibake, which looks like a broken adapter rather than a configuration mismatch. So
/// rather than guess, try the plausible rates and keep the one that produces a reply
/// recognisable as an ELM-compatible identity.
/// </para>
/// </remarks>
public static class BaudNegotiator
{
    /// <summary>
    /// Rates worth trying, in the order most likely to pay off.
    /// </summary>
    /// <remarks>
    /// 115200 is the EX's factory default; 2,000,000 is what the STN2232 reaches and what
    /// FORScan negotiates. The rest cover older adapters and clones.
    /// </remarks>
    public static readonly int[] CandidateRates =
        [115200, 2000000, 1000000, 500000, 230400, 57600, 38400, 9600];

    /// <summary>
    /// Try each rate until one answers coherently.
    /// </summary>
    /// <param name="openAt">
    /// Creates a transport for a given rate. Injected so this is testable without a
    /// physical port.
    /// </param>
    public static async Task<(int BaudRate, IReadOnlyList<BaudAttempt> Attempts)> FindAsync(
        Func<int, IVehicleTransport> openAt,
        IEnumerable<int>? rates = null,
        CancellationToken ct = default)
    {
        var attempts = new List<BaudAttempt>();

        foreach (var rate in rates ?? CandidateRates)
        {
            var transport = openAt(rate);

            try
            {
                await transport.ConnectAsync(ct).ConfigureAwait(false);

                // ATZ first: a reset clears any half-read state from a previous attempt at
                // the wrong rate, which would otherwise poison this one.
                await transport.ExchangeAsync("ATZ", ct).ConfigureAwait(false);
                var reply = await transport.ExchangeAsync("ATI", ct).ConfigureAwait(false);

                var ok = LooksLikeAdapter(reply);
                attempts.Add(new BaudAttempt(rate, ok, Flatten(reply)));

                if (ok)
                {
                    return (rate, attempts);
                }
            }
            catch (IOException ex)
            {
                attempts.Add(new BaudAttempt(rate, false, $"<{ex.Message}>"));
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }

        throw new IOException(
            "No baud rate produced a coherent reply. Tried: " +
            string.Join(", ", attempts.Select(a => $"{a.BaudRate} ({Flatten(a.Reply)})")));
    }

    /// <summary>
    /// Decide whether a reply is a real adapter identity rather than noise.
    /// </summary>
    /// <remarks>
    /// Checking for a known name is not enough on its own, because garbage at the wrong
    /// baud occasionally contains plausible letters. Requiring the reply to be mostly
    /// printable ASCII as well rejects mojibake that happens to pass a substring test.
    /// </remarks>
    public static bool LooksLikeAdapter(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        var printable = reply.Count(c => c is >= ' ' and <= '~' || c is '\r' or '\n');

        if (printable < reply.Length * 0.9)
        {
            return false;
        }

        return reply.Contains("ELM", StringComparison.OrdinalIgnoreCase) ||
               reply.Contains("STN", StringComparison.OrdinalIgnoreCase) ||
               reply.Contains("OBDLINK", StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace(">", string.Empty, StringComparison.Ordinal)
        .Trim();
}
