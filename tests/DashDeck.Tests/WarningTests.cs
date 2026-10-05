using DashDeck.Abstractions;
using DashDeck.Core.Actions;
using DashDeck.Core.Diagnostics;
using DashDeck.Core.Warnings;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>Warning lights, trouble codes, and the one gated write: clearing them (ADR-0055).</summary>
public class WarningTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class MemoryLog : IActionLog
    {
        public List<string> Lines { get; } = [];

        public bool Fail { get; set; }

        public bool Write(DateTimeOffset at, string line)
        {
            if (Fail)
            {
                return false;
            }

            Lines.Add(line);
            return true;
        }
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    [Fact]
    public void Trouble_code_services_are_asked_with_no_pid()
    {
        Assert.Equal("03", PidRequest.Service(ObdService.StoredCodes, CanBus.Hs).ToCommand());
        Assert.Equal("07", PidRequest.Service(ObdService.PendingCodes, CanBus.Hs).ToCommand());
        Assert.Equal("04", PidRequest.Service(ObdService.ClearCodes, CanBus.Hs).ToCommand());
        Assert.Equal("010C", new PidRequest(0x01, 0x0C, CanBus.Hs).ToCommand());
    }

    [Fact]
    public void Parses_stored_codes_and_the_clear_reply()
    {
        var stored = ElmResponseParser.Parse(PidRequest.Service(ObdService.StoredCodes, CanBus.Hs), "43 01 04 20\r\r>", At);
        Assert.True(stored.IsSuccess);
        Assert.Equal([0x01, 0x04, 0x20], stored.Data);

        var none = ElmResponseParser.Parse(PidRequest.Service(ObdService.StoredCodes, CanBus.Hs), "4300\r\r>", At);
        Assert.True(none.IsSuccess);
        Assert.Empty(TroubleCode.FromPayload(none.Data));

        var cleared = ElmResponseParser.Parse(PidRequest.Service(ObdService.ClearCodes, CanBus.Hs), "44\r\r>", At);
        Assert.True(cleared.IsSuccess);
        Assert.Empty(cleared.Data);

        var refused = ElmResponseParser.Parse(PidRequest.Service(ObdService.ClearCodes, CanBus.Hs), "7F 04 22\r\r>", At);
        Assert.Equal(PidFailure.Rejected, refused.Failure);
        Assert.Equal((byte)0x22, refused.NegativeCode);
    }

    [Theory]
    [InlineData(0x0420, "P0420")]
    [InlineData(0x0171, "P0171")]
    [InlineData(0x1234, "P1234")]
    [InlineData(0x4123, "C0123")]
    [InlineData(0x9001, "B1001")]
    [InlineData(0xC100, "U0100")]
    public void Codes_read_as_written(int raw, string text)
    {
        var code = new TroubleCode((ushort)raw);
        Assert.Equal(text, code.Text);
        Assert.True(TroubleCode.TryParse(text.ToLowerInvariant(), out var back));
        Assert.Equal(code, back);
    }

    [Fact]
    public void Generic_and_makers_codes_are_told_apart()
    {
        Assert.True(new TroubleCode(0x0420).IsGeneric);
        Assert.True(new TroubleCode(0x2096).IsGeneric);
        Assert.False(new TroubleCode(0x1234).IsGeneric);
        Assert.False(new TroubleCode(0x9001).IsGeneric);
    }

    [Fact]
    public void Payload_count_and_padding_are_respected()
    {
        // CAN: a count of two, then two codes, then padding that is not a code.
        Assert.Equal(["P0420", "P0171"], TroubleCode.FromPayload([0x02, 0x04, 0x20, 0x01, 0x71, 0x00, 0x00]).Select(c => c.Text));

        // Older buses: pairs alone, zero-padded.
        Assert.Equal(["P0300"], TroubleCode.FromPayload([0x03, 0x00, 0x00, 0x00, 0x00, 0x00]).Select(c => c.Text));
    }

    [Fact]
    public void Shipped_descriptions_name_common_codes_and_groups()
    {
        var (descriptions, problem) = TroubleCodeDescriptions.Load(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(TestCatalog.Path())!, "reference"));
        Assert.Null(problem);
        Assert.Contains("Catalytic", descriptions.Describe(new TroubleCode(0x0420)), StringComparison.Ordinal);
        Assert.Contains("maker", descriptions.Describe(new TroubleCode(0x1234)), StringComparison.Ordinal);
        Assert.Contains("misfire", descriptions.Describe(new TroubleCode(0x0399)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Network communication", TroubleCodeDescriptions.Empty.Describe(new TroubleCode(0xC100)));
    }

    // ── Reading the codes from the synthetic truck ───────────────────────────

    private static async Task<(ElmAdapter Adapter, SimulatedF150 Truck)> TruckAsync(params string[] faults)
    {
        var truck = new SimulatedF150(Drives.ColdStartCity);
        foreach (var fault in faults)
        {
            Assert.Null(truck.Faults.Add(fault));
        }

        var adapter = new ElmAdapter(new SyntheticTransport(truck, faults: SyntheticFaults.Perfect));
        await adapter.InitializeAsync(TestCancellation.Token);
        return (adapter, truck);
    }

    [Fact]
    public async Task Reads_stored_and_pending_codes_from_each_emissions_module()
    {
        var (adapter, _) = await TruckAsync("P0420", "P0171/pending");
        await using var _a = adapter;

        var report = await TroubleCodeReader.ReadAsync(adapter.RequestAsync, TestCatalog.Reference().Modules, SystemClock.Instance, TestCancellation.Token);

        Assert.True(report.AnyAnswered);
        Assert.Equal(2, report.Answered.Count);
        Assert.Equal(["P0420"], report.Stored.Select(c => c.Text));
        Assert.Equal(["P0171"], report.Pending.Select(c => c.Text));
        Assert.Equal((ushort)0x7E0, report.Stored.Single().Module);
    }

    [Fact]
    public async Task Many_codes_come_back_over_several_frames()
    {
        var (adapter, _) = await TruckAsync("P0420", "P0171", "P0300", "P0128");
        await using var _a = adapter;

        var report = await TroubleCodeReader.ReadAsync(adapter.RequestAsync, TestCatalog.Reference().Modules, SystemClock.Instance, TestCancellation.Token);

        Assert.Equal(["P0420", "P0171", "P0300", "P0128"], report.Stored.Select(c => c.Text));
    }

    [Fact]
    public async Task With_no_modules_named_the_broadcast_is_asked()
    {
        var (adapter, _) = await TruckAsync("P0420");
        await using var _a = adapter;

        var report = await TroubleCodeReader.ReadAsync(adapter.RequestAsync, new Dictionary<ushort, string>(), SystemClock.Instance, TestCancellation.Token);

        Assert.Equal(["broadcast"], report.Answered);
        Assert.Equal(["P0420"], report.Stored.Select(c => c.Text));
    }

    [Fact]
    public void Simulated_faults_start_and_stop_on_time()
    {
        var faults = new SimulatedFaults();
        Assert.Null(faults.Add("P0420@10"));
        Assert.Null(faults.Add("warning.oilPressure@5-20"));
        Assert.Null(faults.Add("engine.coolantTemp=118"));
        Assert.NotNull(faults.Add("@@"));

        Assert.Empty(faults.Codes(5, pending: false));
        Assert.Equal(["P0420"], faults.Codes(10, pending: false));
        Assert.False(faults.TryHeld("warning.oilPressure", 4, out _));
        Assert.True(faults.TryHeld("warning.oilPressure", 5, out var oil));
        Assert.Equal(1, oil);
        Assert.False(faults.TryHeld("warning.oilPressure", 20, out _));
        Assert.True(faults.TryHeld("engine.coolantTemp", 0, out var hot));
        Assert.Equal(118, hot);

        faults.ClearCodes(12);
        Assert.Empty(faults.Codes(13, pending: false));
    }

    // ── Clearing: the five gates ─────────────────────────────────────────────

    private static VehicleActions Actions(ElmAdapter adapter, bool enabled, MemoryLog log, IClock clock) =>
        new(adapter.RequestAsync, TestCatalog.Load, () => enabled, log, clock);

    private static ActionConfirmation Held(IClock clock) => new(TimeSpan.FromSeconds(2.2), clock.UtcNow);

    [Fact]
    public async Task Clearing_is_refused_while_the_master_switch_is_off()
    {
        var (adapter, truck) = await TruckAsync("P0420", "engine.rpm=0");
        await using var _a = adapter;
        var log = new MemoryLog();

        var result = await Actions(adapter, enabled: false, log, SystemClock.Instance).ClearDiagnosticCodesAsync(Held(SystemClock.Instance), "P0420", TestCancellation.Token);

        Assert.Equal(ActionOutcome.Refused, result.Outcome);
        Assert.Equal(0, truck.Faults.ClearCount);
        Assert.Contains(log.Lines, l => l.Contains("REFUSED", StringComparison.Ordinal) && l.Contains("switched off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clearing_is_refused_with_the_engine_running()
    {
        var (adapter, truck) = await TruckAsync("P0420");
        await using var _a = adapter;
        var log = new MemoryLog();

        var result = await Actions(adapter, enabled: true, log, SystemClock.Instance).ClearDiagnosticCodesAsync(Held(SystemClock.Instance), "P0420", TestCancellation.Token);

        Assert.Equal(ActionOutcome.Refused, result.Outcome);
        Assert.Contains("engine off", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, truck.Faults.ClearCount);
        Assert.Single(log.Lines);
    }

    [Fact]
    public async Task Clearing_is_refused_without_a_full_recent_hold()
    {
        var (adapter, truck) = await TruckAsync("P0420", "engine.rpm=0");
        await using var _a = adapter;
        var clock = new SettableClock(At);
        var actions = Actions(adapter, enabled: true, new MemoryLog(), clock);

        var tooShort = await actions.ClearDiagnosticCodesAsync(new ActionConfirmation(TimeSpan.FromSeconds(0.5), At), "P0420", TestCancellation.Token);
        var tooOld = await actions.ClearDiagnosticCodesAsync(new ActionConfirmation(TimeSpan.FromSeconds(3), At - TimeSpan.FromSeconds(30)), "P0420", TestCancellation.Token);

        Assert.Equal(ActionOutcome.Refused, tooShort.Outcome);
        Assert.Equal(ActionOutcome.Refused, tooOld.Outcome);
        Assert.Equal(0, truck.Faults.ClearCount);
    }

    [Fact]
    public async Task Stopped_with_the_engine_off_it_clears_once_and_logs_it()
    {
        var (adapter, truck) = await TruckAsync("P0420", "engine.rpm=0");
        await using var _a = adapter;
        var log = new MemoryLog();
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        var actions = Actions(adapter, enabled: true, log, clock);
        var hold = Held(clock);

        var result = await actions.ClearDiagnosticCodesAsync(hold, "stored P0420", TestCancellation.Token);

        Assert.Equal(ActionOutcome.Done, result.Outcome);
        Assert.Equal(1, truck.Faults.ClearCount);
        Assert.False(truck.CheckEngine);
        Assert.Contains(log.Lines, l => l.Contains("SENDING", StringComparison.Ordinal) && l.Contains("stored P0420", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("DONE", StringComparison.Ordinal));

        // The same hold does not confirm a second one.
        var again = await actions.ClearDiagnosticCodesAsync(hold, "none", TestCancellation.Token);
        Assert.Equal(ActionOutcome.Refused, again.Outcome);
        Assert.Equal(1, truck.Faults.ClearCount);
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_log_cannot_be_written()
    {
        var (adapter, truck) = await TruckAsync("P0420", "engine.rpm=0");
        await using var _a = adapter;

        var result = await Actions(adapter, enabled: true, new MemoryLog { Fail = true }, SystemClock.Instance).ClearDiagnosticCodesAsync(Held(SystemClock.Instance), "P0420", TestCancellation.Token);

        Assert.Equal(ActionOutcome.Refused, result.Outcome);
        Assert.Equal(0, truck.Faults.ClearCount);
    }

    [Fact]
    public void The_log_file_appends()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dashdeck-actions-{Guid.NewGuid():N}.log");
        try
        {
            var log = new ActionLogFile(path);
            Assert.True(log.Write(At, "one"));
            Assert.True(log.Write(At, "two"));
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("two", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── The warnings file ────────────────────────────────────────────────────

    private static IReadOnlyList<WarningDefinition> Shipped()
    {
        var (warnings, problems) = WarningCatalog.Load(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(TestCatalog.Path())!, WarningCatalog.FileName));
        Assert.Empty(problems);
        return warnings;
    }

    [Fact]
    public void Shipped_warnings_name_real_signals_and_pop_the_serious_ones()
    {
        var warnings = Shipped();
        var catalog = TestCatalog.Load();

        foreach (var w in warnings)
        {
            Assert.True(catalog.TryGet(w.Signal, out _), $"{w.Id}: {w.Signal} is not in the catalog");
            if (w.CountSignal is { } count)
            {
                Assert.True(catalog.TryGet(count, out _), $"{w.Id}: {count} is not in the catalog");
            }
        }

        Assert.Equal(
            ["brake", "charging", "checkEngine", "coolantHot", "oilPressure"],
            warnings.Where(w => w.Popup).Select(w => w.Id).Order());
        Assert.True(warnings.Single(w => w.Id == "checkEngine").Codes);
    }

    [Fact]
    public void Bad_rows_are_left_out_and_named()
    {
        var (warnings, problems) = WarningCatalog.Parse("""[ { "id": "a", "title": "A", "signal": "x" }, { "title": "no id" }, { "id": "a", "title": "A", "signal": "y" } ]""");
        Assert.Single(warnings);
        Assert.Equal(2, problems.Count);
        Assert.Throws<InvalidDataException>(() => WarningCatalog.Parse("{"));
    }

    [Theory]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s" }""", 1, true)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s" }""", 0, false)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s", "below": 11.8 }""", 11.5, true)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s", "below": 11.8 }""", 12.4, false)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s", "onAt": 112 }""", 113, true)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s", "bit": 2 }""", 4, true)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s", "bit": 2 }""", 3, false)]
    [InlineData("""{ "id": "w", "title": "W", "signal": "s", "equals": 3 }""", 3, true)]
    public void Conditions_read_like_the_console(string row, double value, bool lit)
    {
        var (warnings, _) = WarningCatalog.Parse($"[{row}]");
        Assert.Equal(lit, warnings.Single().IsLit(value));
    }

    // ── The monitor ──────────────────────────────────────────────────────────

    private static readonly WarningDefinition Engine = new()
    {
        Id = "checkEngine", Title = "CHECK ENGINE", Signal = "mil", HoldSeconds = 3, Popup = true, CountSignal = "count",
    };

    private static readonly WarningDefinition Oil = new()
    {
        Id = "oil", Title = "OIL PRESSURE", Signal = "oil", HoldSeconds = 2, Popup = true, Severity = WarningSeverity.Stop,
    };

    private static readonly WarningDefinition Brake = new()
    {
        Id = "brake", Title = "BRAKE", Signal = "brake", HoldSeconds = 0, Popup = true, WhenMoving = true,
    };

    private sealed class Readings
    {
        public Dictionary<string, SignalValue> Values { get; } = [];

        public void Set(string id, double value, SignalQuality quality = SignalQuality.Live) =>
            Values[id] = new SignalValue(id, value, "", At, quality);

        public SignalValue Read(string id) => Values.TryGetValue(id, out var v) ? v : SignalValue.Missing(id);
    }

    private static List<WarningChange> Run(WarningMonitor monitor, Readings readings, ref DateTimeOffset now, int seconds, double? speed = 0)
    {
        var changes = new List<WarningChange>();
        for (var i = 0; i < seconds; i++)
        {
            changes.AddRange(monitor.Update(now, readings.Read, speed).Select(c => c.Change));
            now += TimeSpan.FromSeconds(1);
        }

        return changes;
    }

    [Fact]
    public void A_light_must_hold_before_it_pops()
    {
        var monitor = new WarningMonitor([Engine]);
        var readings = new Readings();
        var now = At;
        readings.Set("mil", 1);

        Assert.Empty(Run(monitor, readings, ref now, 3));
        Assert.Equal([WarningChange.Raised], Run(monitor, readings, ref now, 1));
        Assert.Equal("checkEngine", monitor.Alert(_ => true)?.Id);

        // A blip that does not last changes nothing.
        readings.Set("mil", 0);
        Assert.Empty(Run(monitor, readings, ref now, 2));
        readings.Set("mil", 1);
        Assert.Empty(Run(monitor, readings, ref now, 5));
    }

    [Fact]
    public void No_reading_is_never_off_and_never_lit()
    {
        var monitor = new WarningMonitor([Engine]);
        var readings = new Readings();
        var now = At;

        readings.Set("mil", 1, SignalQuality.Stale);
        Assert.Empty(Run(monitor, readings, ref now, 10));

        readings.Set("mil", 1);
        Run(monitor, readings, ref now, 4);
        Assert.True(monitor.States.Single().IsLit);

        readings.Set("mil", 0, SignalQuality.Unavailable);
        Assert.Empty(Run(monitor, readings, ref now, 30));
        Assert.True(monitor.States.Single().IsLit);
    }

    [Fact]
    public void Dismissed_stays_dismissed_until_it_goes_off_and_on()
    {
        var monitor = new WarningMonitor([Engine]);
        var readings = new Readings();
        var now = At;
        readings.Set("mil", 1);
        readings.Set("count", 1);
        Run(monitor, readings, ref now, 4);

        monitor.Dismiss("checkEngine");
        Assert.Null(monitor.Alert(_ => true));
        Assert.Empty(Run(monitor, readings, ref now, 60));
        Assert.Null(monitor.Alert(_ => true));

        readings.Set("mil", 0);
        Assert.Equal([WarningChange.Cleared], Run(monitor, readings, ref now, 4));

        readings.Set("mil", 1);
        Assert.Equal([WarningChange.Raised], Run(monitor, readings, ref now, 4));
        Assert.NotNull(monitor.Alert(_ => true));
    }

    [Fact]
    public void A_new_code_pops_a_dismissed_check_engine_again()
    {
        var monitor = new WarningMonitor([Engine]);
        var readings = new Readings();
        var now = At;
        readings.Set("mil", 1);
        readings.Set("count", 1);
        Run(monitor, readings, ref now, 4);
        monitor.Dismiss("checkEngine");

        readings.Set("count", 2);
        Assert.Equal([WarningChange.Reraised], Run(monitor, readings, ref now, 1));
        Assert.NotNull(monitor.Alert(_ => true));
    }

    [Fact]
    public void A_dismissal_outlives_the_program_until_the_light_is_seen_off()
    {
        var first = new WarningMonitor([Engine]);
        var readings = new Readings();
        var now = At;
        readings.Set("mil", 1);
        readings.Set("count", 1);
        Run(first, readings, ref now, 4);
        first.Dismiss("checkEngine");

        var second = new WarningMonitor([Engine]);
        second.Restore(first.Dismissals);
        Assert.Empty(Run(second, readings, ref now, 10));
        Assert.True(second.States.Single().IsLit);
        Assert.Null(second.Alert(_ => true));

        readings.Set("mil", 0);
        Run(second, readings, ref now, 4);
        Assert.Empty(second.Dismissals);
    }

    [Fact]
    public void A_when_moving_warning_waits_for_movement()
    {
        var monitor = new WarningMonitor([Brake]);
        var readings = new Readings();
        var now = At;
        readings.Set("brake", 1);

        Assert.Empty(Run(monitor, readings, ref now, 5, speed: 0));
        Assert.Empty(Run(monitor, readings, ref now, 5, speed: null));
        Assert.Equal([WarningChange.Raised], Run(monitor, readings, ref now, 1, speed: 40));
    }

    [Fact]
    public void The_alert_is_red_before_amber_and_only_if_wanted()
    {
        var monitor = new WarningMonitor([Engine, Oil]);
        var readings = new Readings();
        var now = At;
        readings.Set("mil", 1);
        readings.Set("oil", 1);
        Run(monitor, readings, ref now, 5);

        Assert.Equal("oil", monitor.Alert(_ => true)?.Id);
        Assert.Equal("checkEngine", monitor.Alert(d => d.Id != "oil")?.Id);
        Assert.Null(monitor.Alert(_ => false));
        Assert.Equal(["mil", "count", "oil"], monitor.Signals);
    }
}
