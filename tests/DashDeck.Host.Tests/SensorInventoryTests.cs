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
/// editor that defines them into the user's overlay file (ADR-0030).
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

        public SignalPollStatus StatusOf(string signalId) =>
            Statuses.GetValueOrDefault(signalId, SignalPollStatus.NotAsked);

        public Task<PidResponse> ProbeAsync(PidRequest request, CancellationToken ct)
        {
            Asked.Add(request);

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
