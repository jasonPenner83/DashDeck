using System.Text;
using DashDeck.Abstractions;
using DashDeck.Vehicle;

namespace DashDeck.Core.Discovery;

/// <summary>One module that answered the sweep.</summary>
/// <param name="Bus">The bus it answered on.</param>
/// <param name="Address">Its request id — what a signal's <c>module</c> field names.</param>
/// <param name="PartNumber">What it said its part number is, when it said.</param>
/// <param name="RefusalCode">The negative response code, when it declined to say.</param>
public sealed record DiscoveredModule(CanBus Bus, ushort Address, string? PartNumber, byte? RefusalCode);

/// <summary>Where a sweep has got to.</summary>
public readonly record struct SweepProgress(int Done, int Total, int Found, string Current);

/// <summary>What a module sweep found, and why a bus said nothing when one did.</summary>
public sealed record ModuleScanResult(
    IReadOnlyList<DiscoveredModule> Modules,
    IReadOnlyDictionary<CanBus, string> Problems,
    bool Completed);

/// <summary>
/// Finds the modules on each bus by asking every address one harmless question (ADR-0035).
/// </summary>
/// <remarks>
/// The supported-PID scan (ADR-0032) asks the broadcast, and only emissions ECUs answer the
/// broadcast — on the F-150, the engine computer. FORScan lists a dozen and more modules
/// because it addresses each one directly: an 11-bit request id from <c>700</c> to
/// <c>7F7</c>, answered on that id plus eight. This does the same: every address with the 8s
/// bit clear, on HS-CAN and MS-CAN, 128 per bus.
/// <para>
/// The question is UDS <c>22 F113</c> — ReadDataByIdentifier, the module's part number. A
/// read, never a write, and not even a session change or a tester-present: whatever a module
/// is doing, being asked its part number does not change it. <b>Any answer</b> — the part
/// number, or a negative response saying it does not do that — means a module lives there.
/// Only silence means nothing does.
/// </para>
/// </remarks>
public static class ModuleScanner
{
    /// <summary>
    /// ReadDataByIdentifier. The only service the sweeps use, and it only reads.
    /// </summary>
    public const byte ReadDataByIdentifier = 0x22;

    /// <summary>UDS identifier for the ECU's own part number — the question each address is asked.</summary>
    public const ushort PartNumberDid = 0xF113;

    /// <summary>Every module request id: 700–7F7, 8s bit clear. 128 of them.</summary>
    public static IEnumerable<ushort> Addresses()
    {
        for (var id = PidRequest.FirstModule; id <= PidRequest.LastModule; id++)
        {
            if (PidRequest.IsModuleAddress(id))
            {
                yield return id;
            }
        }
    }

    public static async Task<ModuleScanResult> ScanAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> request,
        IReadOnlyList<CanBus> buses,
        IProgress<SweepProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(buses);

        var addresses = Addresses().ToList();
        var total = addresses.Count * buses.Count;
        var found = new List<DiscoveredModule>();
        var problems = new Dictionary<CanBus, string>();
        var done = 0;

        foreach (var bus in buses)
        {
            var silentLink = 0;
            var busErrors = 0;

            foreach (var address in addresses)
            {
                if (ct.IsCancellationRequested)
                {
                    return new ModuleScanResult(found, problems, Completed: false);
                }

                progress?.Report(new SweepProgress(done, total, found.Count, $"{BusName(bus)} {address:X3}"));

                PidResponse response;
                try
                {
                    response = await request(new PidRequest(ReadDataByIdentifier, PartNumberDid, bus, address), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new ModuleScanResult(found, problems, Completed: false);
                }

                done++;

                if (response.IsSuccess)
                {
                    found.Add(new DiscoveredModule(bus, address, Text(response.Data), null));
                }
                else if (response.Failure is PidFailure.Rejected)
                {
                    found.Add(new DiscoveredModule(bus, address, null, response.NegativeCode));
                }
                else if (response.Failure is PidFailure.Timeout)
                {
                    silentLink++;
                }
                else if (response.Failure is PidFailure.BusError)
                {
                    busErrors++;
                }
            }

            if (silentLink == addresses.Count)
            {
                problems[bus] = "no reply from the adapter";
            }
            else if (busErrors == addresses.Count)
            {
                problems[bus] = "the adapter reported a bus error at every address";
            }
            else if (!found.Any(m => m.Bus == bus))
            {
                problems[bus] = "no module answered";
            }
        }

        progress?.Report(new SweepProgress(done, total, found.Count, ""));
        return new ModuleScanResult(found, problems, Completed: true);
    }

    /// <summary>
    /// The printable part of an identifier's reply, or null when there is none — a part
    /// number is ASCII, usually padded with zeros or spaces.
    /// </summary>
    public static string? Text(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);

        foreach (var b in data)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                sb.Append((char)b);
            }
            else if (b != 0x00)
            {
                // Binary, not text: a code, a counter, a bitmap. Shown as bytes elsewhere.
                return null;
            }
        }

        var text = sb.ToString().Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>What a negative response code means, in a few words.</summary>
    public static string DescribeRefusal(byte code) => code switch
    {
        0x10 => "general reject",
        0x11 => "service not supported",
        0x12 => "sub-function not supported",
        0x13 => "wrong length",
        0x22 => "conditions not correct",
        0x31 => "not supported here",
        0x33 => "locked (security access)",
        0x7E or 0x7F => "not in this session",
        _ => $"refused ({code:X2})",
    };

    internal static string BusName(CanBus bus) => bus is CanBus.Ms ? "MS" : "HS";
}

/// <summary>One identifier a module answered, or declined in a way worth knowing about.</summary>
/// <param name="Did">The two-byte identifier: the PID a mode 22 signal names.</param>
/// <param name="Data">What it returned. Empty when it was refused.</param>
/// <param name="RefusalCode">Why it was refused, when the refusal says it exists — locked, or not now.</param>
public sealed record FoundIdentifier(ushort Did, byte[] Data, byte? RefusalCode);

/// <summary>What one identifier sweep found.</summary>
/// <param name="Found">Identifiers that answered, and those that exist but declined.</param>
/// <param name="Asked">How many were asked.</param>
/// <param name="Problem">Why it stopped early, when it did.</param>
public sealed record DidSweepResult(IReadOnlyList<FoundIdentifier> Found, int Asked, string? Problem);

/// <summary>
/// Asks one module for every identifier in a range (ADR-0035).
/// </summary>
/// <remarks>
/// The step after finding a module: which of its 65,536 identifiers does it answer? A module
/// that is there answers every one — with data, or with a negative response — so an unsupported
/// identifier costs one round trip, not a timeout. Ranges are capped at
/// <see cref="MaxSpan"/> so a sweep is minutes, not hours, and a sweep stops by itself when the
/// module goes quiet, because a silent module is either asleep or gone and asking it four
/// thousand more times helps neither.
/// <para>
/// Reads only — <c>22</c>, ReadDataByIdentifier — and the person is asked to be parked: it takes
/// most of the adapter's time while it runs.
/// </para>
/// </remarks>
public static class DidScanner
{
    /// <summary>The most identifiers one sweep asks: 4,096 is about four minutes at the measured rate.</summary>
    public const int MaxSpan = 0x1000;

    /// <summary>Consecutive silences after which the module is taken to have gone away.</summary>
    public const int SilenceLimit = 12;

    /// <summary>Negative responses that only mean "no such identifier" — not worth listing.</summary>
    private static bool MeansAbsent(byte? code) => code is null or 0x31 or 0x11 or 0x12 or 0x13 or 0x10;

    public static async Task<DidSweepResult> ScanAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> request,
        CanBus bus,
        ushort module,
        ushort first,
        ushort last,
        IProgress<SweepProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PidRequest.IsModuleAddress(module))
        {
            throw new ArgumentOutOfRangeException(nameof(module), "not a module address");
        }

        if (last < first || last - first + 1 > MaxSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(last), $"a sweep covers 1 to {MaxSpan} identifiers");
        }

        var total = last - first + 1;
        var found = new List<FoundIdentifier>();
        var silent = 0;
        var asked = 0;

        for (var did = (int)first; did <= last; did++)
        {
            if (ct.IsCancellationRequested)
            {
                return new DidSweepResult(found, asked, "stopped");
            }

            progress?.Report(new SweepProgress(asked, total, found.Count, $"22 {did:X4}"));

            PidResponse response;
            try
            {
                response = await request(
                    new PidRequest(ModuleScanner.ReadDataByIdentifier, (ushort)did, bus, module), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new DidSweepResult(found, asked, "stopped");
            }

            asked++;

            if (response.IsSuccess)
            {
                found.Add(new FoundIdentifier((ushort)did, response.Data, null));
                silent = 0;
            }
            else if (response.Failure is PidFailure.Rejected)
            {
                if (!MeansAbsent(response.NegativeCode))
                {
                    found.Add(new FoundIdentifier((ushort)did, [], response.NegativeCode));
                }

                silent = 0;
            }
            else if (response.Failure is PidFailure.NoData or PidFailure.Timeout)
            {
                if (++silent >= SilenceLimit)
                {
                    return new DidSweepResult(found, asked,
                        $"the module stopped answering at 22 {did:X4} — is the ignition still on?");
                }
            }
        }

        progress?.Report(new SweepProgress(asked, total, found.Count, ""));
        return new DidSweepResult(found, asked, null);
    }
}
