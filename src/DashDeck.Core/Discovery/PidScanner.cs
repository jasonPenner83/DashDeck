using DashDeck.Abstractions;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Diagnostics;

namespace DashDeck.Core.Discovery;

/// <summary>
/// The vehicle's own answer to "which mode 01 PIDs do you support?".
/// </summary>
/// <remarks>
/// OBD-II has a question built in for this. PID <c>00</c> answers a 32-bit bitmap of PIDs
/// <c>01</c>–<c>20</c>; if the last bit is set, PID <c>20</c> answers the next thirty-two, and
/// so on. Asking it costs a handful of requests and replaces guessing — which is the point:
/// a catalog signal the truck does not support only reveals itself by going quiet, and a PID
/// the truck supports that the catalog lacks never reveals itself at all.
/// </remarks>
public static class SupportedPids
{
    /// <summary>The PIDs that answer a bitmap rather than a value: 00, 20, 40 … E0.</summary>
    public static bool IsRangeQuery(int pid) => pid is >= 0 and <= 0xE0 && pid % 0x20 == 0;
}

/// <summary>What one bus said when asked which PIDs it supports.</summary>
/// <param name="Bus">The bus that was asked.</param>
/// <param name="Supported">Value PIDs it advertised — the range queries themselves are left out.</param>
/// <param name="Problem">Why nothing came back, when nothing did; null on an answer.</param>
public sealed record PidScanResult(CanBus Bus, IReadOnlySet<int> Supported, string? Problem)
{
    /// <summary>True when the bus answered at least the first range query.</summary>
    public bool Answered => Problem is null;
}

/// <summary>
/// Walks the supported-PID bitmaps on one bus.
/// </summary>
/// <remarks>
/// The bring-up tool's <see cref="PidSupportScanner"/> asks the same question of a bare
/// adapter, and this shares its bitmap decoding. It differs in two ways the running app needs:
/// it takes the request as a delegate, so it goes through whatever serialises access —
/// <see cref="VehicleService.ProbeAsync"/>, with the polling plan running — and so a test can
/// hand it canned answers; and it asks a dropped bitmap again rather than ending the walk
/// there. It is a one-off question a person asks from Settings, not polling: it never enters
/// the arbiter's plan.
/// </remarks>
public static class PidScanner
{
    /// <summary>
    /// Tries per range query. A dropped response is ordinary on a real adapter, and one unlucky
    /// drop on PID 00 would otherwise report a working bus as silent.
    /// </summary>
    public const int Attempts = 3;

    public static async Task<PidScanResult> ScanAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> request,
        CanBus bus,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var supported = new SortedSet<int>();

        for (var basePid = 0; basePid <= 0xE0; basePid += 0x20)
        {
            var response = await AskAsync(request, new PidRequest(0x01, (ushort)basePid, bus), ct)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Data.Length < 4)
            {
                // Silence on PID 00 means the bus told us nothing. Silence further along means
                // the last bitmap overstated itself, which happens; what was gathered stands.
                return basePid == 0
                    ? new PidScanResult(bus, supported, Describe(response))
                    : new PidScanResult(bus, supported, null);
            }

            var next = false;

            foreach (int pid in PidSupportScanner.DecodeBitmap((ushort)basePid, response.Data))
            {
                if (pid == basePid + 0x20)
                {
                    next = true;
                }
                else if (!SupportedPids.IsRangeQuery(pid))
                {
                    supported.Add(pid);
                }
            }

            if (!next)
            {
                break;
            }
        }

        return new PidScanResult(bus, supported, null);
    }

    private static async Task<PidResponse> AskAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> request,
        PidRequest pid,
        CancellationToken ct)
    {
        PidResponse response = PidResponse.Failed(pid, PidFailure.Timeout, DateTimeOffset.MinValue);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            response = await request(pid, ct).ConfigureAwait(false);

            if (response.IsSuccess || response.Failure is PidFailure.BusError or PidFailure.Malformed)
            {
                break;
            }
        }

        return response;
    }

    private static string Describe(PidResponse response) => response.Failure switch
    {
        PidFailure.NoData => "no answer to PID 00 (NO DATA)",
        PidFailure.BusError => "bus error",
        PidFailure.Timeout => "no reply from the adapter",
        PidFailure.Malformed => "a reply that could not be read",
        _ => "a bitmap too short to read",
    };
}
