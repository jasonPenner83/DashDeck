using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Finding what the truck supports and the catalog lacks, and the user's overlay catalog that
/// adds it (ADR-0032).
/// </summary>
public class DiscoveryTests
{
    private static async Task<VehicleService> StartAsync()
    {
        var transport = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        var service = new VehicleService(new ElmAdapter(transport), TestCatalog.Load());
        await service.StartAsync(TestCancellation.Token);
        return service;
    }

    // ── The bitmap ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x00, true)]
    [InlineData(0x20, true)]
    [InlineData(0xC0, true)]
    [InlineData(0x0C, false)]
    [InlineData(0x21, false)]
    public void Knows_which_pids_are_range_queries(int pid, bool expected) =>
        Assert.Equal(expected, SupportedPids.IsRangeQuery(pid));

    // ── The scan ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_scan_of_the_synthetic_truck_matches_what_it_actually_answers()
    {
        await using var service = await StartAsync();

        var scan = await PidScanner.ScanAsync(service.ProbeAsync, CanBus.Hs, TestCancellation.Token);
        Assert.True(scan.Answered, scan.Problem);

        // Ask every PID directly. The bitmaps must neither overstate nor understate the truck.
        var answered = new SortedSet<int>();
        for (var pid = 1; pid <= 0xFF; pid++)
        {
            if (SupportedPids.IsRangeQuery(pid))
            {
                continue;
            }

            var response = await service.ProbeAsync(new PidRequest(0x01, (ushort)pid, CanBus.Hs), TestCancellation.Token);
            if (response.IsSuccess)
            {
                answered.Add(pid);
            }
        }

        Assert.Equal(answered, scan.Supported);
    }

    [Fact]
    public async Task A_scan_finds_the_pids_the_catalog_lacks()
    {
        await using var service = await StartAsync();
        var catalog = TestCatalog.Load();

        var scan = await PidScanner.ScanAsync(service.ProbeAsync, CanBus.Hs, TestCancellation.Token);
        var defined = catalog.Definitions.Where(d => d.Mode == 1 && d.Bus == CanBus.Hs).Select(d => (int)d.Pid).ToHashSet();
        var missing = scan.Supported.Where(p => !defined.Contains(p)).ToList();

        // Bank 2's trims are on a V6 and deliberately not in the shipped catalog.
        Assert.Contains(0x08, missing);
        Assert.Contains(0x09, missing);
        Assert.DoesNotContain(0x0C, missing);
    }

    [Fact]
    public async Task Body_modules_do_not_answer_the_standard_question()
    {
        await using var service = await StartAsync();

        var scan = await PidScanner.ScanAsync(service.ProbeAsync, CanBus.Ms, TestCancellation.Token);

        Assert.False(scan.Answered);
        Assert.Empty(scan.Supported);
    }

    [Fact]
    public async Task A_dropped_bitmap_is_asked_again_rather_than_reported_as_silence()
    {
        var calls = 0;

        Task<PidResponse> Flaky(PidRequest request, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(calls < PidScanner.Attempts
                ? PidResponse.Failed(request, PidFailure.NoData, DateTimeOffset.UnixEpoch)
                : PidResponse.Ok(request, [0x80, 0x00, 0x00, 0x00], DateTimeOffset.UnixEpoch));
        }

        var scan = await PidScanner.ScanAsync(Flaky, CanBus.Hs, CancellationToken.None);

        Assert.True(scan.Answered);
        Assert.Equal([0x01], scan.Supported);
    }

    [Fact]
    public async Task Polling_status_says_what_the_truck_has_and_has_not_been_asked()
    {
        await using var service = await StartAsync();
        using var speed = service.Bus.Require("vehicle.speed", SignalPriority.High, 20);

        for (var i = 0; i < 100 && service.StatusOf("vehicle.speed") != SignalPollStatus.Answered; i++)
        {
            await Task.Delay(20, TestCancellation.Token);
        }

        Assert.Equal(SignalPollStatus.Answered, service.StatusOf("vehicle.speed"));
        Assert.Equal(SignalPollStatus.NotAsked, service.StatusOf("engine.oilTemp"));
    }

    // ── The standard table ────────────────────────────────────────────────────

    [Fact]
    public void The_standard_table_agrees_with_every_shipped_standard_pid()
    {
        // Two independent transcriptions of J1979: the shipped catalog and the suggestion table.
        // Where both describe a PID, a disagreement means one of them is wrong.
        foreach (var definition in TestCatalog.Load().Definitions.Where(d => d.HasRequest && d.Mode == 1 && d.Bus == CanBus.Hs))
        {
            Assert.True(TestCatalog.Reference().TryGet(definition.Pid, out var entry), $"{definition.Id} is not in the table");

            if (entry.Decode is not { } decode)
            {
                continue;
            }

            Assert.Equal(definition.Decode.ByteLength, decode.ByteLength);
            Assert.Equal(definition.Decode.Signed, decode.Signed);
            Assert.Equal(definition.Decode.Scale, decode.Scale, 6);
            Assert.Equal(definition.Decode.Offset, decode.Offset, 6);
        }
    }

    [Fact]
    public void The_reference_files_load_and_know_the_standard_table()
    {
        var folder = Path.GetDirectoryName(TestCatalog.Path());
        var (reference, problems) = ObdReference.Load(folder);

        Assert.Empty(problems);
        Assert.True(reference.Mode01.Count > 150);
        Assert.True(reference.TryGet(0x0C, out var rpm));
        Assert.Equal(0.25, rpm.Decode!.Scale);
        Assert.False(reference.TryGet(0x13, out var o2) && o2.HasDecode);
        Assert.Equal("Mode 01 PID E5", reference.NameOf(0xE5));
    }

    [Fact]
    public void Every_suggestion_is_a_valid_definition()
    {
        for (var pid = 1; pid <= 0xFF; pid++)
        {
            var suggestion = TestCatalog.Reference().Suggest(pid, CanBus.Hs);
            Assert.Empty(SignalCatalog.Check(suggestion));
        }
    }

    [Fact]
    public void An_unknown_pid_is_suggested_as_a_raw_byte_to_be_worked_out()
    {
        var suggestion = TestCatalog.Reference().Suggest(0xE5, CanBus.Hs);

        Assert.False(TestCatalog.Reference().TryGet(0xE5, out _));
        Assert.Equal("obd2.pidE5", suggestion.Id);
        Assert.Equal(1, suggestion.Decode.Scale);
    }

    // ── The overlay ───────────────────────────────────────────────────────────

    private static SignalDefinition Def(string id, ushort pid, double scale = 1) => new()
    {
        Id = id,
        Name = id,
        Pid = pid,
        Decode = new DecodeSpec(0, 1, false, scale, 0, "°C"),
    };

    [Fact]
    public void An_overlay_replaces_a_shipped_definition_and_adds_new_ones()
    {
        var shipped = TestCatalog.Load();
        var merged = SignalCatalog.Overlay(shipped, [Def("engine.oilTemp", 0x5C, 2), Def("fuel.shortTermTrimB2", 0x08)]);

        Assert.Equal(shipped.Count + 1, merged.Count);
        Assert.Equal(2, merged["engine.oilTemp"].Decode.Scale);
        Assert.True(merged.TryGet("fuel.shortTermTrimB2", out _));
    }

    [Fact]
    public void An_overlay_is_validated_with_the_catalog()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            SignalCatalog.Overlay(TestCatalog.Load(), [Def("x", 1), Def("x", 2)]));

        Assert.Contains("duplicate signal id", ex.Message, StringComparison.Ordinal);
        Assert.NotEmpty(SignalCatalog.Check(Def("y", 1, scale: 0)));
    }

    [Fact]
    public void Definitions_round_trip_through_the_file_format()
    {
        var original = new[] { Def("engine.oilTemp", 0x5C) with { Bus = CanBus.Ms, Min = -40, Category = "Temperature" } };

        var json = SignalCatalog.ToJson(original);
        var back = SignalCatalog.ParseList(json);

        Assert.Equal(original[0], Assert.Single(back));
        Assert.Contains("\"°C\"", json, StringComparison.Ordinal);       // readable, not escaped
        Assert.Contains("\"Ms\"", json, StringComparison.Ordinal);        // the bus by name
        Assert.DoesNotContain("stalenessBudget", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"max\"", json, StringComparison.Ordinal); // nulls left out
    }
}
