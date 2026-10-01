using DashDeck.Abstractions;
using DashDeck.Simulator;
using DashDeck.Vehicle.Diagnostics;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// The bring-up diagnostics decide what we believe about the truck, so a quiet error here
/// would send us chasing imaginary problems in a cold cab.
/// </summary>
public class BringUpDiagnosticsTests
{
    [Fact]
    public void Bitmap_bit_order_is_msb_first_from_the_base_pid()
    {
        // 0x80 00 00 00 sets only the most significant bit, which means PID 0x01.
        Assert.Equal<ushort[]>([0x01], [.. PidSupportScanner.DecodeBitmap(0x00, [0x80, 0x00, 0x00, 0x00])]);

        // 0x00 00 00 01 sets only the least significant bit, which means PID 0x20.
        Assert.Equal<ushort[]>([0x20], [.. PidSupportScanner.DecodeBitmap(0x00, [0x00, 0x00, 0x00, 0x01])]);

        // Getting this order backwards yields a plausible but entirely wrong support list.
        Assert.Equal<ushort[]>([0x41], [.. PidSupportScanner.DecodeBitmap(0x40, [0x80, 0x00, 0x00, 0x00])]);
    }

    [Fact]
    public void Bitmap_decodes_a_realistic_mixed_response()
    {
        // BE 3E B8 11 is the support bitmap a great many vehicles return for range 0x00.
        var pids = PidSupportScanner.DecodeBitmap(0x00, [0xBE, 0x3E, 0xB8, 0x11]);

        Assert.Contains((ushort)0x01, pids);   // monitor status — set on essentially every vehicle
        Assert.Contains((ushort)0x05, pids);   // coolant
        Assert.Contains((ushort)0x0C, pids);   // engine rpm
        Assert.Contains((ushort)0x0D, pids);   // vehicle speed

        // 0x02 (freeze DTC) is clear in this bitmap, and 0x08 and 0x09 fall on byte
        // boundaries — the places an off-by-one in the bit walk would show up.
        Assert.DoesNotContain((ushort)0x02, pids);
        Assert.DoesNotContain((ushort)0x08, pids);
        Assert.DoesNotContain((ushort)0x09, pids);
    }

    [Fact]
    public async Task Scanner_walks_the_ranges_and_finds_what_the_ecu_implements()
    {
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.Idle), faults: SyntheticFaults.Perfect);

        await using var adapter = new ElmAdapter(transport);
        await adapter.InitializeAsync(TestCancellation.Token);

        var report = await PidSupportScanner.ScanAsync(adapter, CanBus.Hs, TestCancellation.Token);

        Assert.True(report.AnyResponse);

        // It must chain past the first range, or everything above PID 0x20 is invisible.
        Assert.Contains((ushort)0x00, report.RangesProbed);
        Assert.Contains((ushort)0x20, report.RangesProbed);
        Assert.Contains((ushort)0x40, report.RangesProbed);

        // The fuel-rate PID is the one open question Q4 turns on.
        Assert.True(report.Supports(0x5E), "0x5E should be reported as supported by the synthetic ECU");

        // Range markers are not data.
        Assert.DoesNotContain((ushort)0x20, report.DataPids);
    }

    [Fact]
    public void Declared_support_list_matches_what_the_simulator_actually_implements()
    {
        // Drift guard: the declared list exists so building a bitmap does not consume
        // random numbers and break determinism. This keeps the two honest.
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.Idle), faults: SyntheticFaults.Perfect);

        var declared = SyntheticTransport.SupportedHsPids.ToHashSet();

        for (var pid = 0; pid <= 0xFF; pid++)
        {
            var implemented = transport.ImplementsHsPid((byte)pid);

            // The range-query PIDs are answered by the bitmap builder, not by EncodePid.
            if (pid is 0x00 or 0x20 or 0x40 or 0x60 or 0x80)
            {
                continue;
            }

            Assert.Equal(implemented, declared.Contains((byte)pid));
        }
    }

    [Fact]
    public async Task Bench_probe_reports_a_healthy_adapter_and_no_vehicle()
    {
        // The situation this tool was written for: adapter on a desk, powered from USB,
        // nothing plugged into the OBD-II port.
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.Idle), faults: SyntheticFaults.BenchNoVehicle);

        var report = await AdapterProbe.RunAsync(transport, "SIM", 115200, TestCancellation.Token);

        Assert.True(report.AdapterHealthy, "the adapter itself should check out even with no vehicle");
        Assert.True(report.SupportsStCommands);
        Assert.True(report.MsCanSwitchAccepted);
        Assert.Equal(VehiclePresence.NotDetected, report.Vehicle);

        // Voltage comes from vehicle power, so a bench run must read low. This is what
        // separates "not in a vehicle" from "in a vehicle with the ignition off".
        Assert.NotNull(report.ObdVoltage);
        Assert.StartsWith("0", report.ObdVoltage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_detects_an_adapter_that_cannot_reach_ms_can()
    {
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.Idle), faults: SyntheticFaults.HsCanOnly);

        var report = await AdapterProbe.RunAsync(transport, "SIM", 115200, TestCancellation.Token);

        Assert.False(report.MsCanSwitchAccepted);
        Assert.True(report.AdapterHealthy);
    }

    [Fact]
    public async Task Probe_keeps_the_full_transcript_for_remote_diagnosis()
    {
        var transport = new SyntheticTransport(
            new SimulatedF150(Drives.Idle), faults: SyntheticFaults.BenchNoVehicle);

        var report = await AdapterProbe.RunAsync(transport, "SIM", 115200, TestCancellation.Token);

        Assert.Contains(report.Steps, s => s.Command == "ATI");
        Assert.Contains(report.Steps, s => s.Command == "STP53");
        Assert.Contains(report.Steps, s => s.Command == "0100");
        Assert.All(report.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Note)));
    }
}

/// <summary>
/// Opening a serial port at the wrong baud rate does not fail — it succeeds and returns
/// mojibake, which reads as a broken adapter rather than a configuration mismatch. These
/// cover the check that tells the two apart.
/// </summary>
public class BaudNegotiatorTests
{
    [Theory]
    [InlineData("ELM327 v1.4b\r\r>", true)]
    [InlineData("STN2232 v5.12.4\r\r>", true)]
    [InlineData("OBDLink EX r2.7.1\r\r>", true)]
    [InlineData("", false)]
    [InlineData("\xff\xfe\x80\x81ELM\xff\xff\xff\xff\xff", false)]
    [InlineData("OK\r\r>", false)]
    public void Recognises_a_real_identity_and_rejects_noise(string reply, bool expected)
    {
        Assert.Equal(expected, BaudNegotiator.LooksLikeAdapter(reply));
    }

    [Fact]
    public void Tries_the_factory_default_first()
    {
        // 115200 is what the OBDLink EX ships at, so the common case must not pay for the
        // search. 2 Mbps follows because that is what FORScan negotiates on this adapter.
        Assert.Equal(115200, BaudNegotiator.CandidateRates[0]);
        Assert.Contains(2000000, BaudNegotiator.CandidateRates);
    }

    [Fact]
    public async Task Walks_past_rates_that_return_garbage()
    {
        var tried = new List<int>();

        var (found, attempts) = await BaudNegotiator.FindAsync(
            rate =>
            {
                tried.Add(rate);

                // Only 2 Mbps answers coherently — the state the adapter is left in after
                // FORScan has raised its UART rate and not reverted it.
                return rate == 2000000
                    ? new SyntheticTransport(
                        new SimulatedF150(Drives.Idle), faults: SyntheticFaults.BenchNoVehicle)
                    : new GarbageTransport();
            },
            rates: [115200, 230400, 2000000],
            ct: TestCancellation.Token);

        Assert.Equal(2000000, found);
        Assert.Equal([115200, 230400, 2000000], tried);
        Assert.Equal(3, attempts.Count);
        Assert.False(attempts[0].Succeeded);
    }

    /// <summary>A port opened at the wrong rate: it works, and says nothing meaningful.</summary>
    private sealed class GarbageTransport : DashDeck.Vehicle.IVehicleTransport
    {
        public DashDeck.Vehicle.TransportState State => DashDeck.Vehicle.TransportState.Connected;

        public string Description => "garbage";

        public event Action<DashDeck.Vehicle.TransportState>? StateChanged;

        public Task ConnectAsync(CancellationToken ct)
        {
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<string> ExchangeAsync(string command, CancellationToken ct) =>
            Task.FromResult("ÿþ\u0080\u0081ÿÿÿ>");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// The rule that decides whether the shell talks to a truck or a simulation (ADR-0031).
/// Getting it wrong either kills the dash on a desk or quietly simulates in the truck.
/// </summary>
public class AdapterSelectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_means_simulate(string? configured)
    {
        // The setting is free text. A stray space pasted into it must not become an attempt
        // to open a device named nothing, which would take the dash down on a desk.
        Assert.False(AdapterSelection.TryResolvePort(configured, out var port));
        Assert.Equal(string.Empty, port);
    }

    [Theory]
    [InlineData("COM7", "COM7")]
    [InlineData("  COM7 ", "COM7")]
    [InlineData("com7", "com7")]
    public void Surrounding_whitespace_is_dropped(string configured, string expected)
    {
        Assert.True(AdapterSelection.TryResolvePort(configured, out var port));
        Assert.Equal(expected, port);
    }

    [Fact]
    public void Case_is_preserved_because_posix_device_paths_are_case_sensitive()
    {
        // This layer is cross-platform — the bring-up tool runs on Linux. Case-folding is
        // harmless for a Windows COM name and turns /dev/ttyUSB0 into a port that does not
        // exist.
        Assert.True(AdapterSelection.TryResolvePort(" /dev/ttyUSB0 ", out var port));
        Assert.Equal("/dev/ttyUSB0", port);
    }
}
