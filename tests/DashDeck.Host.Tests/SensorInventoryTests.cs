using System.IO;
using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Host.Settings;
using DashDeck.Host.ViewModels;
using DashDeck.Vehicle;

namespace DashDeck.Host.Tests;

/// <summary>
/// The Settings ▸ Sensors section: the inventory, the scan that finds missing signals, and the
/// editor that defines them into the user's overlay file (ADR-0032).
/// </summary>
public sealed class SensorInventoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-signals-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "signals.user.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SignalDefinition Def(string id, ushort pid, string name = "Thing", double scale = 1, string category = "Engine") => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Pid = pid,
        Decode = new DecodeSpec(0, 1, false, scale, 0, "°C"),
        Min = -40,
        Max = 215,
    };

    private static SignalCatalog Shipped() => SignalCatalog.FromDefinitions(
    [
        Def("engine.coolantTemp", 0x05, "Coolant"),
        Def("engine.oilTemp", 0x5C, "Oil"),
        Def("vehicle.speed", 0x0D, "Speed", category: "Speed & Distance"),
    ]);

    /// <summary>A running pipeline, as far as the inventory can tell.</summary>
    private sealed class FakeVehicle(SignalCatalog shipped, SignalCatalog? running = null) : ISignalInventorySource
    {
        public Dictionary<string, SignalPollStatus> Statuses { get; } = new(StringComparer.Ordinal);

        public Dictionary<ushort, byte[]> Answers { get; } = [];

        public List<PidRequest> Asked { get; } = [];

        public SignalCatalog Shipped { get; } = shipped;

        public SignalCatalog Catalog { get; } = running ?? shipped;

        public string? OverlayError { get; init; }

        public IVehicleSignals Signals { get; } = new Components.FakeSignals();

        public IReadOnlyList<VehiclePack> ActivePacks { get; init; } = [];

        public bool IsSimulated { get; init; }

        /// <summary>What an addressed module answers, by module and identifier (ADR-0035).</summary>
        public Dictionary<(ushort Module, ushort Did), byte[]> ModuleAnswers { get; } = [];

        public SignalPollStatus StatusOf(string signalId) =>
            Statuses.GetValueOrDefault(signalId, SignalPollStatus.NotAsked);

        public Task<PidResponse> ProbeAsync(PidRequest request, CancellationToken ct)
        {
            Asked.Add(request);

            if (request.Header is { } module)
            {
                return Task.FromResult(ModuleAnswers.TryGetValue((module, request.Pid), out var reply)
                    ? PidResponse.Ok(request, reply, DateTimeOffset.UnixEpoch)
                    : PidResponse.Refused(request, 0x31, DateTimeOffset.UnixEpoch));
            }

            // HS-CAN only, like the synthetic truck: the body modules do not answer the bitmaps.
            return Task.FromResult(request.Bus is CanBus.Hs && Answers.TryGetValue(request.Pid, out var data)
                ? PidResponse.Ok(request, data, DateTimeOffset.UnixEpoch)
                : PidResponse.Failed(request, PidFailure.NoData, DateTimeOffset.UnixEpoch));
        }
    }

    private static IEnumerable<SignalRowViewModel> Rows(SensorInventoryViewModel inventory) =>
        inventory.Groups.SelectMany(g => g.Rows);

    private static SignalRowViewModel Row(SensorInventoryViewModel inventory, string id) =>
        Rows(inventory).Single(r => r.Id == id);

    // ── The store ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_store_writes_what_it_reads()
    {
        var store = new UserSignalStore(FilePath);
        store.Upsert(Def("fuel.shortTermTrimB2", 0x08));
        store.Upsert(Def("fuel.shortTermTrimB2", 0x08, scale: 0.5));

        var reloaded = new UserSignalStore(FilePath);

        Assert.Equal(0.5, Assert.Single(reloaded.Definitions).Decode.Scale);
        Assert.True(reloaded.Remove("fuel.shortTermTrimB2"));
        Assert.Empty(new UserSignalStore(FilePath).Definitions);
    }

    [Fact]
    public void A_corrupt_file_is_an_empty_overlay_and_a_reason_never_a_crash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var store = new UserSignalStore(FilePath);

        Assert.Empty(store.Definitions);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void A_bad_overlay_is_dropped_whole_and_said_so()
    {
        var (catalog, error) = VehicleStack.ApplyOverlay(Shipped(), [Def("x", 1), Def("x", 2)]);

        Assert.Equal(Shipped().Count, catalog.Count);
        Assert.Contains("duplicate", error, StringComparison.Ordinal);
    }

    // ── Show ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Lists_every_signal_grouped_with_where_it_came_from()
    {
        var store = new UserSignalStore(FilePath);
        store.Upsert(Def("engine.oilTemp", 0x5C, "Oil", scale: 2));
        store.Upsert(Def("fuel.shortTermTrimB2", 0x08, "Trim B2", category: "Fuel"));

        var running = SignalCatalog.Overlay(Shipped(), store.Definitions);
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped(), running), store);

        Assert.Equal(["ENGINE", "FUEL", "SPEED & DISTANCE"], inventory.Groups.Select(g => g.Name.ToUpperInvariant()));
        Assert.Equal(SignalOrigin.Shipped, Row(inventory, "engine.coolantTemp").Origin);
        Assert.Equal("CORRECTED", Row(inventory, "engine.oilTemp").OriginLabel);
        Assert.Equal("YOURS", Row(inventory, "fuel.shortTermTrimB2").OriginLabel);
        Assert.False(inventory.RestartNeeded);
    }

    [Fact]
    public void Status_says_what_the_truck_said_without_asking_it()
    {
        var vehicle = new FakeVehicle(Shipped());
        vehicle.Statuses["engine.oilTemp"] = SignalPollStatus.Retired;

        var inventory = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath));

        Assert.Equal("NO ANSWER", Row(inventory, "engine.oilTemp").StatusText);
        Assert.Equal("NOT ASKED", Row(inventory, "vehicle.speed").StatusText);
        Assert.Empty(vehicle.Asked);
    }

    [Fact]
    public void The_filter_narrows_by_name_id_or_group()
    {
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), new UserSignalStore(FilePath))
        {
            Filter = "speed",
        };

        Assert.Equal(["vehicle.speed"], Rows(inventory).Select(r => r.Id));
    }

    // ── Find ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_scan_is_set_against_the_catalog_both_ways()
    {
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), new UserSignalStore(FilePath));

        inventory.ApplyScan(
        [
            new PidScanResult(CanBus.Hs, new SortedSet<int> { 0x05, 0x08, 0x0D, 0xE5 }, null),
            new PidScanResult(CanBus.Ms, new SortedSet<int>(), "no answer to PID 00 (NO DATA)"),
        ]);

        Assert.Equal([0x08, 0xE5], inventory.Missing.Select(m => m.Pid));
        Assert.True(inventory.Missing[0].HasStandardDecode);
        Assert.False(inventory.Missing[1].HasStandardDecode);
        Assert.Equal(["engine.oilTemp"], inventory.Unsupported.Select(r => r.Id));
        Assert.Contains("MS-CAN: no answer", inventory.ScanStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scanning_asks_the_bitmaps_and_finds_the_missing_pid()
    {
        var vehicle = new FakeVehicle(Shipped());

        // 0x08 in byte A marks PID 05; 0x88 in byte B marks 09 and 0D. The last bit is clear,
        // so nothing past 20 is asked.
        vehicle.Answers[0x00] = [0x08, 0x88, 0x00, 0x00];

        var inventory = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath));
        await inventory.ScanCommand.ExecuteAsync(null);

        Assert.True(inventory.HasScanned);
        Assert.Equal([0x09], inventory.Missing.Select(m => m.Pid));
        Assert.All(vehicle.Asked, r => Assert.Equal(0x01, r.Mode));
    }

    // ── Define ────────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_a_missing_pid_starts_from_the_standard_and_saves_to_the_overlay()
    {
        var store = new UserSignalStore(FilePath);
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), store);
        inventory.ApplyScan([new PidScanResult(CanBus.Hs, new SortedSet<int> { 0x08 }, null)]);

        inventory.AddMissingCommand.Execute(inventory.Missing[0]);
        var editor = inventory.Editor!;

        Assert.True(editor.IsNew);
        Assert.Equal("fuel.shortTermTrimB2", editor.Id);
        Assert.False(editor.HasProblems, editor.Message);

        editor.SaveCommand.Execute(null);

        Assert.Null(inventory.Editor);
        Assert.Empty(inventory.Missing);
        Assert.Equal("fuel.shortTermTrimB2", Assert.Single(store.Definitions).Id);
        Assert.Equal("NEXT LAUNCH", Row(inventory, "fuel.shortTermTrimB2").StatusText);
        Assert.True(inventory.RestartNeeded);
    }

    [Fact]
    public void Correcting_a_shipped_signal_overrides_it_and_reverting_brings_it_back()
    {
        var store = new UserSignalStore(FilePath);
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), store);

        inventory.EditCommand.Execute(Row(inventory, "engine.oilTemp"));
        var editor = inventory.Editor!;
        Assert.True(editor.IsIdLocked);
        Assert.False(editor.CanRemove);

        editor.ScaleText = "2";
        editor.SaveCommand.Execute(null);

        Assert.Equal(SignalOrigin.Override, Row(inventory, "engine.oilTemp").Origin);

        inventory.EditCommand.Execute(Row(inventory, "engine.oilTemp"));
        Assert.Equal("REVERT TO SHIPPED", inventory.Editor!.RemoveCaption);
        inventory.Editor.RemoveCommand.Execute(null);

        Assert.Equal(SignalOrigin.Shipped, Row(inventory, "engine.oilTemp").Origin);
        Assert.Empty(store.Definitions);
        Assert.False(inventory.RestartNeeded);
    }

    [Fact]
    public void The_editor_refuses_a_bad_definition_as_it_is_typed()
    {
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), new UserSignalStore(FilePath));
        inventory.NewCommand.Execute(null);
        var editor = inventory.Editor!;

        // Blank to begin with.
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Id = "vehicle.speed";
        editor.Name = "Speed again";
        editor.PidText = "F40D";
        Assert.Contains("already exists", editor.Message, StringComparison.Ordinal);

        editor.Id = "trans.fluidTemp";
        editor.PidText = "XYZ";
        Assert.Contains("PID must be hex", editor.Message, StringComparison.Ordinal);

        editor.PidText = "0x F4 0D";
        editor.ScaleText = "0";
        Assert.Contains("scale", editor.Message, StringComparison.Ordinal);

        editor.ScaleText = "1";
        Assert.True(editor.SaveCommand.CanExecute(null), editor.Message);
    }

    [Fact]
    public async Task Test_asks_the_truck_once_and_decodes_with_the_formula_as_typed()
    {
        var vehicle = new FakeVehicle(Shipped());
        vehicle.Answers[0xF40D] = [0x00, 0x7B];

        var inventory = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath));
        inventory.NewCommand.Execute(null);
        var editor = inventory.Editor!;

        editor.ModeText = "22";
        editor.PidText = "F40D";
        editor.ByteOffsetText = "1";
        editor.OffsetText = "-40";
        editor.Unit = "°C";

        await editor.TestCommand.ExecuteAsync(null);

        var request = Assert.Single(vehicle.Asked);
        Assert.Equal(new PidRequest(0x22, 0xF40D, CanBus.Hs), request);
        Assert.Equal("00 7B", editor.TestRaw);
        Assert.Equal("Decodes to 83 °C.", editor.TestDecoded);

        // The formula is re-applied to the same bytes as it is edited, without asking again.
        editor.MaxText = "50";
        Assert.Contains("outside min/max", editor.TestDecoded, StringComparison.Ordinal);
        Assert.Single(vehicle.Asked);
    }

    [Fact]
    public async Task A_test_the_truck_does_not_answer_says_so()
    {
        var vehicle = new FakeVehicle(Shipped());
        var inventory = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath));
        inventory.NewCommand.Execute(null);
        var editor = inventory.Editor!;
        editor.PidText = "1234";

        await editor.TestCommand.ExecuteAsync(null);

        Assert.StartsWith("NO DATA", editor.TestRaw, StringComparison.Ordinal);
        Assert.Equal("", editor.TestDecoded);
    }

    // ── Modules (ADR-0035) ────────────────────────────────────────────────────

    private static readonly VehiclePack FordPack = new()
    {
        Name = "test",
        Match = new VehiclePackMatch { Make = "Ford" },
        Modules = new Dictionary<string, string> { ["726"] = "BCM — body control" },
    };

    private static ModuleScanResult TwoModules() => new(
        [
            new DiscoveredModule(CanBus.Ms, 0x726, "SYNTH-BCM", null),
            new DiscoveredModule(CanBus.Hs, 0x7E0, null, 0x31),
        ],
        new Dictionary<CanBus, string>(),
        Completed: true);

    [Fact]
    public void Found_modules_are_listed_hs_first_with_a_likely_name_that_says_it_is_likely()
    {
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()) { ActivePacks = [FordPack] }, new UserSignalStore(FilePath));
        var modules = inventory.Modules;

        modules.ApplyModules(TwoModules());

        Assert.Equal([0x7E0, 0x726], modules.Found.Select(m => (int)m.Address));
        Assert.Contains("BCM", modules.Found[1].Caption, StringComparison.Ordinal);
        Assert.Contains("likely", modules.Found[1].Detail, StringComparison.Ordinal);
        Assert.Contains("SYNTH-BCM", modules.Found[1].Detail, StringComparison.Ordinal);
        Assert.Contains("HS-CAN: 1 module", modules.Status, StringComparison.Ordinal);
        Assert.Contains("MS-CAN: 1 module", modules.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sweeping_a_module_asks_it_by_address_and_a_find_opens_the_editor_ready_to_test()
    {
        var vehicle = new FakeVehicle(Shipped()) { IsSimulated = true };
        vehicle.ModuleAnswers[(0x726, 0x4001)] = [0x00, 0x8D];

        var store = new UserSignalStore(FilePath);
        var inventory = new SensorInventoryViewModel(vehicle, store);
        var modules = inventory.Modules;
        modules.ApplyModules(TwoModules());

        modules.SelectCommand.Execute(modules.Found[1]);
        modules.FromText = "4000";
        modules.ToText = "400F";
        Assert.True(modules.SweepCommand.CanExecute(null));

        await modules.SweepCommand.ExecuteAsync(null);

        Assert.All(vehicle.Asked, r => Assert.Equal((ushort)0x726, r.Header));
        Assert.Equal(16, vehicle.Asked.Count);
        var found = Assert.Single(modules.Identifiers);
        Assert.Equal("22 4001", found.Caption);
        Assert.True(found.CanDefine);

        modules.DefineCommand.Execute(found);
        var editor = inventory.Editor!;
        Assert.Equal("726", editor.ModuleText);
        Assert.True(editor.IsMsCan);
        Assert.Equal("22 4001 → 726", editor.RequestText);
        Assert.False(editor.HasProblems, editor.Message);

        // TEST goes to the module too.
        vehicle.Asked.Clear();
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal(new PidRequest(0x22, 0x4001, CanBus.Ms, 0x726), Assert.Single(vehicle.Asked));
        Assert.Equal("00 8D", editor.TestRaw);

        editor.SaveCommand.Execute(null);
        Assert.Equal("726", Assert.Single(store.Definitions).Module);
    }

    [Fact]
    public void A_range_too_wide_or_backwards_cannot_be_swept()
    {
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), new UserSignalStore(FilePath));
        var modules = inventory.Modules;
        modules.ApplyModules(TwoModules());
        modules.SelectCommand.Execute(modules.Found[0]);

        Assert.True(modules.SweepCommand.CanExecute(null));

        modules.FromText = "0000";
        modules.ToText = "1000";
        Assert.False(modules.SweepCommand.CanExecute(null));
        Assert.NotNull(modules.RangeProblem);

        modules.ToText = "0FFF";
        Assert.Null(modules.RangeProblem);

        modules.FromText = "2000";
        Assert.False(modules.SweepCommand.CanExecute(null));
    }

    // ── Kept across launches ──────────────────────────────────────────────────

    private string DiscoveryPath => Path.Combine(_dir, "discovery.json");

    private static readonly IClock At = new FixedClock(new DateTimeOffset(2026, 10, 2, 21, 18, 0, TimeSpan.Zero));

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    [Fact]
    public void The_discovery_store_writes_what_it_reads()
    {
        var store = new DiscoveryStore(DiscoveryPath);
        store.SaveModules(TwoModules(), At.UtcNow);
        store.SaveSweep(CanBus.Hs, 0x7E0, 0xF400, 0xF4FF,
            new DidSweepResult([new FoundIdentifier(0xF405, [0x88], null), new FoundIdentifier(0xF41F, [], 0x33)], 256, null), At.UtcNow);

        var reloaded = new DiscoveryStore(DiscoveryPath);
        var modules = reloaded.LoadModules()!;

        Assert.Equal(At.UtcNow, reloaded.ModulesScannedUtc);
        Assert.Equal([(CanBus.Ms, (ushort)0x726, "SYNTH-BCM", (byte?)null), (CanBus.Hs, (ushort)0x7E0, (string?)null, (byte?)0x31)],
            modules.Modules.Select(m => (m.Bus, m.Address, m.PartNumber, m.RefusalCode)));

        var (sweep, swept) = reloaded.LoadSweep(CanBus.Hs, 0x7E0, 0xF400, 0xF4FF)!.Value;
        Assert.Equal(At.UtcNow, swept);
        Assert.Equal(256, sweep.Asked);
        Assert.Equal([0x88], sweep.Found[0].Data);
        Assert.Equal((byte)0x33, sweep.Found[1].RefusalCode);
        Assert.Equal(["F400–F4FF"], reloaded.SweptRanges(CanBus.Hs, 0x7E0));
        Assert.Null(reloaded.LoadSweep(CanBus.Hs, 0x7E0, 0x1000, 0x1FFF));
    }

    [Fact]
    public void Sweeping_the_same_range_again_replaces_it_and_other_ranges_stay()
    {
        var store = new DiscoveryStore(DiscoveryPath);
        store.SaveSweep(CanBus.Hs, 0x7E0, 0xF400, 0xF4FF, new DidSweepResult([], 256, null), At.UtcNow);
        store.SaveSweep(CanBus.Hs, 0x7E0, 0x1000, 0x1FFF, new DidSweepResult([], 4096, null), At.UtcNow);
        store.SaveSweep(CanBus.Hs, 0x7E0, 0xF400, 0xF4FF, new DidSweepResult([new FoundIdentifier(0xF405, [0x90], null)], 256, null), At.UtcNow);

        var reloaded = new DiscoveryStore(DiscoveryPath);

        Assert.Equal(["1000–1FFF", "F400–F4FF"], reloaded.SweptRanges(CanBus.Hs, 0x7E0));
        Assert.Equal([0x90], Assert.Single(reloaded.LoadSweep(CanBus.Hs, 0x7E0, 0xF400, 0xF4FF)!.Value.Result.Found).Data);
    }

    [Fact]
    public void A_corrupt_discovery_file_is_nothing_saved_never_a_crash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(DiscoveryPath, "{ not json");

        var store = new DiscoveryStore(DiscoveryPath);

        Assert.Null(store.LoadModules());
        Assert.NotNull(store.LastError);
    }

    /// <summary>The point of it: after a restart, the modules and what they answered are still there.</summary>
    [Fact]
    public async Task A_sweep_on_the_truck_is_there_after_a_restart()
    {
        var vehicle = new FakeVehicle(Shipped());
        vehicle.ModuleAnswers[(0x7E0, 0x4001)] = [0x00, 0x8D];
        new DiscoveryStore(DiscoveryPath).SaveModules(TwoModules(), At.UtcNow);

        var first = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath), discovery: new DiscoveryStore(DiscoveryPath), clock: At).Modules;
        Assert.Equal(2, first.Found.Count);
        Assert.StartsWith("SAVED SCAN", first.Status, StringComparison.Ordinal);

        first.SelectCommand.Execute(first.Found[0]);
        first.FromText = "4000";
        first.ToText = "400F";
        await first.SweepCommand.ExecuteAsync(null);
        Assert.Contains("swept 4000–400F", first.Found[0].Detail, StringComparison.Ordinal);

        // Launch again.
        var again = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath), discovery: new DiscoveryStore(DiscoveryPath), clock: At).Modules;
        Assert.Contains("swept 4000–400F", again.Found[0].Detail, StringComparison.Ordinal);

        again.SelectCommand.Execute(again.Found[0]);
        Assert.Empty(again.Identifiers);
        Assert.Contains("has not been swept", again.SweepStatus, StringComparison.Ordinal);

        again.FromText = "4000";
        again.ToText = "400F";
        again.SelectCommand.Execute(again.Found[0]);
        Assert.Equal("22 4001", Assert.Single(again.Identifiers).Caption);
        Assert.StartsWith("SAVED SWEEP", again.SweepStatus, StringComparison.Ordinal);
    }

    /// <summary>The synthetic truck's modules would overwrite the real ones the first time the dash ran at a desk.</summary>
    [Fact]
    public async Task The_synthetic_truck_is_never_saved()
    {
        var vehicle = new FakeVehicle(Shipped()) { IsSimulated = true };
        vehicle.ModuleAnswers[(0x726, 0x4001)] = [0x01];
        var store = new DiscoveryStore(DiscoveryPath);
        var modules = new SensorInventoryViewModel(vehicle, new UserSignalStore(FilePath), discovery: store, clock: At).Modules;
        modules.ApplyModules(TwoModules());

        modules.SelectCommand.Execute(modules.Found[1]);
        modules.FromText = "4000";
        modules.ToText = "400F";
        await modules.SweepCommand.ExecuteAsync(null);

        Assert.Contains("not saved", modules.SweepStatus, StringComparison.Ordinal);
        Assert.Empty(new DiscoveryStore(DiscoveryPath).SweptRanges(CanBus.Ms, 0x726));
        Assert.Null(new DiscoveryStore(DiscoveryPath).LoadModules());
    }

    [Fact]
    public void The_editor_refuses_a_reply_address_as_a_module()
    {
        var inventory = new SensorInventoryViewModel(new FakeVehicle(Shipped()), new UserSignalStore(FilePath));
        inventory.NewCommand.Execute(null);
        var editor = inventory.Editor!;
        editor.Id = "body.thing";
        editor.Name = "Thing";
        editor.PidText = "4001";

        editor.ModuleText = "72E";
        Assert.Contains("module", editor.Message, StringComparison.Ordinal);
        Assert.False(editor.TestCommand.CanExecute(null));

        editor.ModuleText = "";
        Assert.True(editor.SaveCommand.CanExecute(null), editor.Message);
        Assert.Contains("broadcast", editor.ModuleHint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0C", 0xFF, true, 0x0C)]
    [InlineData("0x22", 0xFF, true, 0x22)]
    [InlineData("F4 0D", 0xFFFF, true, 0xF40D)]
    [InlineData("100", 0xFF, false, 0)]
    [InlineData("", 0xFF, false, 0)]
    [InlineData("-1", 0xFF, false, 0)]
    public void Reads_hex_the_way_pids_are_written(string text, int max, bool ok, int expected)
    {
        Assert.Equal(ok, SignalEditorViewModel.TryHex(text, max, out var value));

        if (ok)
        {
            Assert.Equal(expected, value);
        }
    }
}
