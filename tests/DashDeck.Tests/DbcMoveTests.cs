using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Dbc;

namespace DashDeck.Tests;

/// <summary>
/// Moving the person's established signals into their own CAN databases (ADR-0078). Invented
/// modules, identifiers and scalings throughout (ADR-0045, ADR-0052).
/// </summary>
public class DbcMoveTests
{
    private static readonly SignalCatalog Shipped = TestCatalog.Load();

    private static IReadOnlyList<SignalDefinition> Overlay(params string[] entries) =>
        SignalCatalog.ParseList("[" + string.Join(",", entries) + "]");

    private static string Entry(string id, string where, string extra = "") => $$"""
        { "id": "{{id}}", "name": "Name of {{id}}", "category": "Engine", "defaultRateHz": 2, "min": -40, "max": 215,
          {{where}}{{extra}} }
        """;

    private const string OilTemp = """
        "bus": "Hs", "mode": 34, "pid": 4660, "module": "726",
        "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": -40, "unit": "°C" }
        """;

    private static DbcMoveResult Plan(IReadOnlyList<SignalDefinition> overlay, IReadOnlyDictionary<string, DbcDocument>? there = null) =>
        DbcMove.Plan(overlay, Shipped, file => there?.GetValueOrDefault(file));

    /// <summary>What the next launch reads: each database written out as text and read back, the overlay as JSON.</summary>
    private static List<SignalDefinition> NextLaunch(DbcMoveResult result, IReadOnlyDictionary<string, DbcDocument>? there = null)
    {
        var files = new Dictionary<string, DbcDocument>(there ?? new Dictionary<string, DbcDocument>());

        foreach (var (file, db) in result.Databases)
        {
            var (read, problems) = DbcReader.Read(DbcWriter.Write(db));
            Assert.Empty(problems);
            files[file] = read;
        }

        return SignalCatalog.ParseList(SignalCatalog.ToJson(result.Overlay), DbcLibrary.Of(files));
    }

    [Fact]
    public void A_confirmed_signal_of_yours_moves_and_reads_back_the_same()
    {
        var before = Overlay(Entry("ford.oilTemp", OilTemp));

        var result = Plan(before);

        Assert.Equal([("ford.oilTemp", "truck-hs.dbc")], result.Moved);
        Assert.Empty(result.Stayed);
        Assert.Equal(CanBus.Hs, DiagnosticAnswers.BusOf(result.Databases["truck-hs.dbc"]));

        var after = Assert.Single(NextLaunch(result));
        Assert.Equal(new DbcReference("truck-hs.dbc", "Module_726", "ford_oilTemp"), after.Dbc);
        Assert.Equal(before[0].ToRequest(), after.ToRequest());
        Assert.Equal(before[0].Decode, after.Decode);

        // The use stays in the catalog.
        Assert.Equal((before[0].Name, before[0].Category, before[0].DefaultRateHz, before[0].Min, before[0].Max),
            (after.Name, after.Category, after.DefaultRateHz, after.Min, after.Max));

        // And it is what the overlay holds now, so nothing reads as changed until a restart re-reads it.
        Assert.Equal(after, Assert.Single(result.Overlay));
    }

    [Fact]
    public void The_catalog_keeps_the_reference_alone()
    {
        var json = SignalCatalog.ToJson(Plan(Overlay(Entry("ford.oilTemp", OilTemp))).Overlay);

        Assert.Contains("\"dbc\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"decode\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pid\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"module\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_database_names_the_value_so_another_tool_can_say_what_it_is()
    {
        var db = Plan(Overlay(Entry("ford.oilTemp", OilTemp))).Databases["truck-hs.dbc"];
        var text = DbcWriter.Write(db);

        Assert.Contains("BO_ 1838 Module_726", text, StringComparison.Ordinal);   // 726 + 8 = 72E, where it answers
        Assert.Contains("\"Name of ford.oilTemp\"", text, StringComparison.Ordinal);
        Assert.Contains("\"°C\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_bus_has_its_own_file()
    {
        var result = Plan(Overlay(
            Entry("ford.oilTemp", OilTemp),
            Entry("ford.door", """
                "bus": "Ms", "mode": 34, "pid": 4096, "module": "740",
                "decode": { "byteOffset": 1, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "", "mask": 32 }
                """)));

        Assert.Equal([("ford.oilTemp", "truck-hs.dbc"), ("ford.door", "truck-ms.dbc")], result.Moved);
        Assert.Equal(CanBus.Ms, DiagnosticAnswers.BusOf(result.Databases["truck-ms.dbc"]));

        var door = NextLaunch(result).Single(d => d.Id == "ford.door");
        Assert.Equal((CanBus.Ms, (ushort?)0x740), (door.Bus, door.ModuleAddress));
        Assert.Equal(new DecodeSpec(1, 1, false, 1, 0, "", 32), door.Decode);
    }

    [Fact]
    public void States_go_in_as_the_value_table_and_come_back()
    {
        var before = Overlay(Entry("ford.fourWd", """
            "bus": "Hs", "mode": 34, "pid": 8192, "module": "760",
            "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "" },
            "states": [ { "value": 0, "name": "2H" }, { "value": 1, "name": "4A" }, { "value": 2, "name": "4H" }, { "value": 3, "name": "4L" } ]
            """));

        var result = Plan(before);

        Assert.Single(result.Moved);
        Assert.Equal(before[0].States, Assert.Single(NextLaunch(result)).States);
        Assert.Contains("VAL_ ", DbcWriter.Write(result.Databases["truck-hs.dbc"]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_between_steps_of_the_value_stays()
    {
        var result = Plan(Overlay(Entry("ford.odd", """
            "bus": "Hs", "mode": 34, "pid": 8193, "module": "760",
            "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 2, "offset": 0, "unit": "" },
            "states": [ { "value": 3, "name": "Half" } ]
            """)));

        Assert.Empty(result.Moved);
        Assert.Contains("not a whole step", Assert.Single(result.Stayed).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_01_value_of_yours_is_the_engine_answering_the_broadcast()
    {
        var before = Overlay(Entry("my.boost", """
            "bus": "Hs", "mode": 1, "pid": 112,
            "decode": { "byteOffset": 0, "byteLength": 2, "signed": false, "scale": 0.03125, "offset": 0, "unit": "kPa" }
            """));

        var result = Plan(before);
        var after = Assert.Single(NextLaunch(result));

        Assert.Equal("Module_7E0", after.Dbc!.Message);
        Assert.Null(after.Module);
        Assert.Equal(before[0].ToRequest(), after.ToRequest());
    }

    [Fact]
    public void An_ID_found_for_a_shipped_placeholder_moves()
    {
        Assert.True(Shipped["tire.frontLeft.pressure"].Placeholder);

        var result = Plan(Overlay(Entry("tire.frontLeft.pressure", """
            "bus": "Ms", "mode": 34, "pid": 10529, "module": "751",
            "decode": { "byteOffset": 0, "byteLength": 2, "signed": false, "scale": 0.1, "offset": 0, "unit": "kPa" }
            """)));

        Assert.Equal([("tire.frontLeft.pressure", "truck-ms.dbc")], result.Moved);
    }

    [Fact]
    public void Renaming_or_hiding_a_standard_signal_is_not_moved_and_not_mentioned()
    {
        var rpm = Shipped["engine.rpm"] with { Name = "Revs", Hidden = true };

        var result = Plan([rpm]);

        Assert.Empty(result.Moved);
        Assert.Empty(result.Stayed);
        Assert.Same(rpm, Assert.Single(result.Overlay));
    }

    [Fact]
    public void A_correction_to_a_standard_signal_moves()
    {
        var rpm = Shipped["engine.rpm"] with { Decode = Shipped["engine.rpm"].Decode with { Scale = 0.5 } };

        Assert.Single(Plan([rpm]).Moved);
    }

    [Fact]
    public void Unconfirmed_signals_stay_until_tested()
    {
        var result = Plan(Overlay(Entry("ford.guess", OilTemp, """, "unconfirmed": true""")));

        Assert.Empty(result.Moved);
        Assert.Empty(result.Databases);
        Assert.Contains("unconfirmed", Assert.Single(result.Stayed).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_22_asked_of_every_module_stays()
    {
        var result = Plan(Overlay(Entry("ford.anyone", """
            "bus": "Hs", "mode": 34, "pid": 4660,
            "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "" }
            """)));

        Assert.Empty(result.Moved);
        Assert.Contains("every module", Assert.Single(result.Stayed).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decode_a_database_cannot_describe_stays()
    {
        var result = Plan(Overlay(Entry("ford.gappy", """
            "bus": "Hs", "mode": 34, "pid": 4661, "module": "726",
            "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "", "mask": 5 }
            """)));

        Assert.Empty(result.Moved);
        Assert.Contains("no field", Assert.Single(result.Stayed).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_already_there_is_added_to_and_keeps_what_it_had()
    {
        var mine = DiagnosticAnswers.Put(DiagnosticAnswers.WithBus(DbcDocument.Empty, CanBus.Hs), 0x7E0, 0x22, 0xABCD,
            DiagnosticAnswers.Layout(new DecodeSpec(0, 1, false, 1, 0, ""), 0x22, "AlreadyHere")!);
        var there = new Dictionary<string, DbcDocument> { ["truck-hs.dbc"] = mine };

        var result = Plan(Overlay(Entry("ford.oilTemp", OilTemp)), there);
        var answers = DiagnosticAnswers.Read(result.Databases["truck-hs.dbc"]).Answers.Select(a => a.Signal.Name).ToList();

        Assert.Contains("AlreadyHere", answers);
        Assert.Contains("ford_oilTemp", answers);
    }

    [Fact]
    public void A_database_on_the_other_bus_is_not_written_into()
    {
        var there = new Dictionary<string, DbcDocument> { ["truck-hs.dbc"] = DiagnosticAnswers.WithBus(DbcDocument.Empty, CanBus.Ms) };

        var result = Plan(Overlay(Entry("ford.oilTemp", OilTemp)), there);

        Assert.Empty(result.Moved);
        Assert.Empty(result.Databases);
        Assert.Contains("says it is MS-CAN", Assert.Single(result.Stayed).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_ids_that_make_the_same_name_in_one_module_are_told_apart()
    {
        var result = Plan(Overlay(
            Entry("ford.oil-temp", OilTemp),
            Entry("ford.oil_temp", OilTemp.Replace("4660", "4662", StringComparison.Ordinal))));

        Assert.Equal(["ford_oil_temp", "ford_oil_temp_2"], NextLaunch(result).Select(d => d.Dbc!.Signal));
    }

    [Fact]
    public void Moving_again_moves_nothing()
    {
        var once = Plan(Overlay(Entry("ford.oilTemp", OilTemp)));

        var twice = DbcMove.Plan(NextLaunch(once), Shipped, once.Databases.GetValueOrDefault);

        Assert.Empty(twice.Moved);
        Assert.Empty(twice.Stayed);
        Assert.Empty(twice.Databases);
    }

    [Fact]
    public void Every_signal_in_the_shipped_catalog_could_move_without_changing_what_the_dash_asks()
    {
        // The widest check there is: every real request the dash ships, written into a database and
        // read back. Anything that cannot go says why rather than coming back different.
        var all = Shipped.Definitions.Where(d => d.HasRequest).Select(d => d with { Id = "copy." + d.Id }).ToList();

        var result = DbcMove.Plan(all, Shipped, _ => null);
        var back = NextLaunch(result).ToDictionary(d => d.Id);

        Assert.NotEmpty(result.Moved);
        Assert.Equal(all.Count, result.Moved.Count + result.Stayed.Count);

        foreach (var (id, _) in result.Moved)
        {
            var original = all.Single(d => d.Id == id);
            Assert.Equal((original.ToRequest(), original.Decode), (back[id].ToRequest(), back[id].Decode));
        }
    }
}
