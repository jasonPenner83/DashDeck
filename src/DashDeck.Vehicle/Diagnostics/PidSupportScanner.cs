using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Diagnostics;

/// <summary>Which mode 01 PIDs the vehicle says it supports.</summary>
public sealed record PidSupportReport(
    CanBus Bus,
    IReadOnlySet<ushort> SupportedPids,
    IReadOnlyList<ushort> RangesProbed,
    bool AnyResponse)
{
    public bool Supports(ushort pid) => SupportedPids.Contains(pid);

    /// <summary>
    /// Supported PIDs excluding the range-query markers.
    /// </summary>
    /// <remarks>
    /// 0x00, 0x20, 0x40 and so on are the "which PIDs do you support" queries themselves.
    /// The vehicle does flag them as supported and the scan needs them to know whether to
    /// keep walking, but they carry no vehicle data — so counting them inflates any report
    /// of how much this truck can actually tell us.
    /// </remarks>
    public IReadOnlyList<ushort> DataPids =>
        [.. SupportedPids.Where(p => p % 0x20 != 0).OrderBy(p => p)];
}

/// <summary>
/// Asks the vehicle which PIDs it answers, instead of guessing.
/// </summary>
/// <remarks>
/// Mode 01 PID <c>0x00</c> returns a four-byte bitmap covering PIDs <c>0x01</c>–<c>0x20</c>,
/// MSB first; <c>0x20</c> covers the next 32, and so on. Each bitmap's last bit says
/// whether the following range is worth asking for, so the scan walks the chain.
/// <para>
/// This settles open question Q4 — whether this truck supports the fuel-rate PID
/// (<c>0x5E</c>) — with an answer from the vehicle rather than an assumption, and it tells
/// us up front which catalog signals will never produce data on this truck.
/// </para>
/// </remarks>
public static class PidSupportScanner
{
    private static readonly ushort[] RangeBasePids = [0x00, 0x20, 0x40, 0x60, 0x80, 0xA0, 0xC0];

    public static async Task<PidSupportReport> ScanAsync(
        IVehicleAdapter adapter,
        CanBus bus,
        CancellationToken ct)
    {
        var supported = new HashSet<ushort>();
        var probed = new List<ushort>();
        var anyResponse = false;

        foreach (var basePid in RangeBasePids)
        {
            // Only ask for a range the previous bitmap said exists. The first is always asked.
            if (basePid != 0x00 && !supported.Contains(basePid))
            {
                break;
            }

            probed.Add(basePid);

            var response = await adapter
                .RequestAsync(new PidRequest(0x01, basePid, bus), ct)
                .ConfigureAwait(false);

            if (!response.IsSuccess || response.Data.Length < 4)
            {
                break;
            }

            anyResponse = true;

            foreach (var pid in DecodeBitmap(basePid, response.Data))
            {
                supported.Add(pid);
            }
        }

        return new PidSupportReport(bus, supported, probed, anyResponse);
    }

    /// <summary>
    /// Expand a four-byte support bitmap into PID numbers.
    /// </summary>
    /// <remarks>
    /// Bit 0 is the most significant bit of the first byte and refers to
    /// <c>basePid + 1</c>, running to <c>basePid + 32</c> at the least significant bit of
    /// the fourth byte. Getting this order backwards produces a plausible-looking but
    /// entirely wrong support list, so it is covered by tests.
    /// </remarks>
    public static IReadOnlyList<ushort> DecodeBitmap(ushort basePid, byte[] bitmap)
    {
        var pids = new List<ushort>();

        for (var index = 0; index < 32 && (index / 8) < bitmap.Length; index++)
        {
            var bitInByte = 7 - (index % 8);

            if ((bitmap[index / 8] & (1 << bitInByte)) != 0)
            {
                pids.Add((ushort)(basePid + 1 + index));
            }
        }

        return pids;
    }
}
