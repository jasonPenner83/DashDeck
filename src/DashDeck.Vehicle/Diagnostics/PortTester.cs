namespace DashDeck.Vehicle.Diagnostics;

/// <summary>What testing one serial port found.</summary>
public enum PortTestOutcome
{
    /// <summary>An OBD-II adapter answered.</summary>
    Adapter,

    /// <summary>The port opened, but nothing on it answered like an adapter at any rate.</summary>
    NoAdapter,

    /// <summary>Another program holds it — FORScan, or DashDeck's own phone GPS.</summary>
    InUse,

    /// <summary>The port would not open, or did not open in time.</summary>
    Unavailable,

    /// <summary>Deliberately not opened: it belongs to something else in DashDeck.</summary>
    Skipped,
}

/// <summary>What one port turned out to be.</summary>
/// <param name="Port">The port, e.g. <c>COM3</c>.</param>
/// <param name="Outcome">What was found.</param>
/// <param name="Identity">The adapter's reply to <c>ATI</c>, when one answered.</param>
/// <param name="BaudRate">The rate it answered at.</param>
/// <param name="Voltage">Its reply to <c>ATRV</c> — the voltage at the OBD-II port — e.g. <c>12.4V</c>.</param>
/// <param name="Detail">One line for a person.</param>
public sealed record PortTestResult(
    string Port,
    PortTestOutcome Outcome,
    string? Identity,
    int? BaudRate,
    string? Voltage,
    string Detail)
{
    public bool IsAdapter => Outcome is PortTestOutcome.Adapter;
}

/// <summary>
/// Opens a serial port briefly and asks whether an OBD-II adapter is on it (ADR-0034).
/// </summary>
/// <remarks>
/// What builds the list of tested ports in Settings ▸ Vehicle. Adapter commands only —
/// <c>ATZ</c>, <c>ATI</c> and <c>ATRV</c> — and nothing to the vehicle, so it is safe to run with
/// the ignition on, off, or with no truck at all. The voltage reads the OBD-II port's pin 16:
/// near zero on a desk, about 12 V with the ignition off, about 14 V running — which tells a
/// person in the cab whether the adapter is actually seated.
/// </remarks>
public static class PortTester
{
    /// <summary>Rates to try. Short: a port test runs while someone watches.</summary>
    public static readonly int[] Rates = [115200, 2000000, 38400, 9600];

    public static async Task<PortTestResult> TestAsync(
        string port,
        Func<string, int, IVehicleTransport> open,
        int? knownBaudRate = null,
        CancellationToken ct = default)
    {
        var rates = (knownBaudRate is { } known ? new[] { known } : []).Concat(Rates).Distinct();

        foreach (var rate in rates)
        {
            ct.ThrowIfCancellationRequested();
            var transport = open(port, rate);

            try
            {
                try
                {
                    await transport.ConnectAsync(ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return ex.InnerException is UnauthorizedAccessException
                        ? new PortTestResult(port, PortTestOutcome.InUse, null, null, null, "In use by another program — close FORScan or OBDwiz to test it.")
                        : new PortTestResult(port, PortTestOutcome.Unavailable, null, null, null,
                            ex.InnerException is TimeoutException ? "Didn't open in time — a Bluetooth port, perhaps." : "Couldn't be opened.");
                }

                try
                {
                    if (Clean(await transport.ExchangeAsync("ATZ", ct).ConfigureAwait(false)).Length == 0)
                    {
                        continue;   // silence at this rate: skip the rest of the handshake
                    }

                    // ATZ turns echo back on; without this the identity reads "ATI ELM327…".
                    await transport.ExchangeAsync("ATE0", ct).ConfigureAwait(false);
                    var identity = Clean(await transport.ExchangeAsync("ATI", ct).ConfigureAwait(false));

                    if (!BaudNegotiator.LooksLikeAdapter(identity))
                    {
                        continue;
                    }

                    var voltage = Clean(await transport.ExchangeAsync("ATRV", ct).ConfigureAwait(false));
                    voltage = voltage.EndsWith('V') && voltage.Length <= 8 ? voltage : null;

                    var detail = voltage is null
                        ? $"{identity} · {rate} baud"
                        : $"{identity} · {rate} baud · {voltage} at the OBD port{VoltageHint(voltage)}";

                    return new PortTestResult(port, PortTestOutcome.Adapter, identity, rate, voltage, detail);
                }
                catch (IOException)
                {
                    // Dropped at this rate; the next may do better.
                }
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new PortTestResult(port, PortTestOutcome.NoAdapter, null, null, null, "Opened, but nothing answered as an OBD-II adapter.");
    }

    /// <summary>What the voltage says about where the adapter is.</summary>
    private static string VoltageHint(string voltage) =>
        double.TryParse(voltage.TrimEnd('V'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var volts)
            ? volts switch
            {
                < 6 => " (not in a vehicle)",
                < 13.2 => " (ignition off)",
                _ => " (engine running)",
            }
            : "";

    private static string Clean(string reply) => reply
        .Replace(">", string.Empty, StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();
}
