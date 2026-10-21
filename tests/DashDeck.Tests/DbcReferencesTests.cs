using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Dbc;
using DashDeck.Core.Discovery.Matching;

namespace DashDeck.Tests;

/// <summary>
/// Catalog entries that take where a value is from our own CAN database (ADR-0074). Invented
/// modules, identifiers and scalings throughout (ADR-0045, ADR-0052).
/// </summary>
public class DbcReferencesTests
{
    private const string File = "truck-ms.dbc";

    private static DbcDocument Database(CanBus? bus = CanBus.Ms)
    {
        var db = bus is { } b ? DiagnosticAnswers.WithBus(DbcDocument.Empty, b) : DbcDocument.Empty;

        db = DiagnosticAnswers.Put(db, 0x7E0, 0x22, 0xABCD, DiagnosticAnswers.Layout(new DecodeSpec(0, 1, false, 1, -40, "°C"), 0x22, "Warmth")!);
        db = DiagnosticAnswers.Put(db, 0x7E0, 0x01, 0x0D, DiagnosticAnswers.Layout(new DecodeSpec(0, 1, false, 1, 0, "km/h"), 0x01, "Pace")!);
        db = DiagnosticAnswers.Put(db, 0x726, 0x22, 0x1234, DiagnosticAnswers.Layout(new DecodeSpec(0, 1, false, 1, 0, ""), 0x22, "Mode")! with
        {
            Values = [new DbcValueText(0, "Off"), new DbcValueText(1, "Low"), new DbcValueText(2, "High")],
        });

        // An Intel field across two bytes: a DBC can say it, the catalog's decode cannot yet.
        db = DiagnosticAnswers.Put(db, 0x726, 0x22, 0x1235, new DbcSignal("Wide", 32, 16, false, false, 1, 0, 0, 0, ""));

        return db;
    }

    private static DbcLibrary Library(DbcDocument? db = null) =>
        DbcLibrary.Of(new Dictionary<string, DbcDocument> { [File] = db ?? Database() });

    private static string Entry(string id, string message, string signal, string extra = "") => $$"""
        { "id": "{{id}}", "name": "{{id}}", "category": "Engine", "defaultRateHz": 2, "min": -40, "max": 215,
          "dbc": { "file": "{{File}}", "message": "{{message}}", "signal": "{{signal}}" }{{extra}} }
        """;

    private static SignalDefinition One(string json, DbcLibrary? library = null) =>
        Assert.Single(SignalCatalog.ParseList($"[{json}]", library ?? Library()));

    [Fact]
    public void A_reference_takes_the_request_and_decode_from_the_database()
    {
        var warmth = One(Entry("x.warmth", "Module_7E0", "Warmth"));

        Assert.Equal((CanBus.Ms, (byte)0x22, (ushort)0xABCD, "7E0"), (warmth.Bus, warmth.Mode, warmth.Pid, warmth.Module));
        Assert.Equal(new DecodeSpec(0, 1, false, 1, -40, "°C"), warmth.Decode);
        Assert.False(warmth.Placeholder);
        Assert.Null(warmth.DbcProblem);

        // What the catalog says is still the catalog's.
        Assert.Equal(("Engine", 2.0, -40.0, 215.0), (warmth.Category, warmth.DefaultRateHz, warmth.Min!.Value, warmth.Max!.Value));

        // And it decodes an answer as a hand-written definition would.
        Assert.Equal(50, warmth.Decode.Decode([90]));
    }

    [Fact]
    public void Mode_01_to_the_engine_is_asked_of_the_broadcast()
    {
        var pace = One(Entry("x.pace", "Module_7E0", "Pace"));

        Assert.Equal(((byte)0x01, (ushort)0x0D), (pace.Mode, pace.Pid));
        Assert.Null(pace.Module);
    }

    [Fact]
    public void A_value_table_becomes_the_signal_s_states()
    {
        var mode = One(Entry("x.mode", "Module_726", "Mode"));

        Assert.Equal("726", mode.Module);
        Assert.Equal([new SignalState(0, "Off"), new SignalState(1, "Low"), new SignalState(2, "High")], mode.States);
        Assert.Equal("High", mode.StateName(2));
    }

    [Fact]
    public void References_make_a_valid_catalog_on_the_database_s_bus()
    {
        var json = $"[{Entry("x.warmth", "Module_7E0", "Warmth")}, {Entry("x.pace", "Module_7E0", "Pace")}]";
        var catalog = SignalCatalog.FromJson(json, Library());

        Assert.Equal(2, catalog.Count);
        Assert.Equal([CanBus.Ms], catalog.RequiredBuses);
    }

    public static TheoryData<string, string, string> Unanswerable => new()
    {
        { "Module_7E0", "Nope", "Module_7E0 in truck-ms.dbc has no signal Nope" },
        { "Module_999", "Warmth", "truck-ms.dbc has no message Module_999" },
        { "Module_726", "Wide", "Module_726.Wide is laid out in a way the catalog cannot decode yet" },
        { "Module_7E0", "Service", "Module_7E0.Service is not an answer to mode 01 or 22" },
    };

    [Theory]
    [MemberData(nameof(Unanswerable))]
    public void A_reference_the_database_cannot_answer_loads_as_a_placeholder_saying_why(string message, string signal, string why)
    {
        var definition = One(Entry("x.lost", message, signal));

        Assert.True(definition.Placeholder);
        Assert.False(definition.HasRequest);
        Assert.StartsWith(why, definition.DbcProblem, StringComparison.Ordinal);

        // It still makes a catalog, so every other signal keeps working.
        Assert.Single(SignalCatalog.FromDefinitions([definition]).Definitions);
    }

    [Fact]
    public void A_missing_file_and_an_unnamed_bus_are_reasons_too()
    {
        Assert.StartsWith("there is no database folder",
            One(Entry("x.warmth", "Module_7E0", "Warmth"), DbcLibrary.None).DbcProblem, StringComparison.Ordinal);

        Assert.StartsWith("truck-ms.dbc does not say which bus",
            One(Entry("x.warmth", "Module_7E0", "Warmth"), Library(Database(bus: null))).DbcProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_written_wrong_is_refused()
    {
        var both = Entry("x.both", "Module_7E0", "Warmth", """, "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "" }""");
        var withPid = Entry("x.pid", "Module_7E0", "Warmth", """, "pid": 13""");
        const string neither = """{ "id": "x.neither", "name": "Neither" }""";
        var climbs = Entry("x.climbs", "Module_7E0", "Warmth").Replace(File, "../elsewhere.dbc", StringComparison.Ordinal);

        Assert.Contains("leave them out", Assert.Throws<InvalidDataException>(() => SignalCatalog.ParseList($"[{both}]", Library())).Message);
        Assert.Contains("leave them out", Assert.Throws<InvalidDataException>(() => SignalCatalog.ParseList($"[{withPid}]", Library())).Message);
        Assert.Contains("decode is missing", Assert.Throws<InvalidDataException>(() => SignalCatalog.ParseList($"[{neither}]", Library())).Message);
        Assert.Contains("file name (no folder)", Assert.Throws<InvalidDataException>(() => SignalCatalog.ParseList($"[{climbs}]", Library())).Message);
    }

    [Fact]
    public void Writing_a_reference_back_leaves_the_database_s_part_out()
    {
        var resolved = SignalCatalog.ParseList($"[{Entry("x.mode", "Module_726", "Mode")}]", Library());
        var json = SignalCatalog.ToJson(resolved);

        Assert.Contains("\"dbc\"", json);
        foreach (var field in new[] { "\"decode\"", "\"pid\"", "\"module\"", "\"mode\"", "\"bus\"", "\"states\"", "\"placeholder\"" })
        {
            Assert.DoesNotContain(field, json);
        }

        // And reads back as the same definition.
        var again = Assert.Single(SignalCatalog.ParseList(json, Library()));
        Assert.Equal(resolved[0] with { States = null }, again with { States = null });
        Assert.Equal(resolved[0].States, again.States);
        Assert.Equal(json, SignalCatalog.ToJson([again]));
    }

    [Fact]
    public void A_reference_that_could_not_be_followed_is_written_back_as_it_was()
    {
        var lost = SignalCatalog.ParseList($"[{Entry("x.warmth", "Module_7E0", "Warmth")}]", DbcLibrary.None);
        var json = SignalCatalog.ToJson(lost);

        Assert.DoesNotContain("placeholder", json);
        Assert.False(Assert.Single(SignalCatalog.ParseList(json, Library())).Placeholder);   // found once the file is there
    }

    [Fact]
    public void Following_a_reference_again_changes_nothing()
    {
        var once = SignalCatalog.ParseList($"[{Entry("x.warmth", "Module_7E0", "Warmth")}]", Library());

        Assert.Equal(once, DbcReferences.Resolve(once, DbcLibrary.None));
    }

    [Fact]
    public void Hiding_a_referenced_signal_keeps_its_reference()
    {
        var warmth = One(Entry("x.warmth", "Module_7E0", "Warmth"));
        var json = SignalCatalog.ToJson(SignalWorkbench.Hide([], warmth));

        Assert.Contains("\"hidden\": true", json);
        Assert.Contains("\"dbc\"", json);
        Assert.DoesNotContain("\"decode\"", json);
    }

    [Fact]
    public void An_edit_keeps_the_reference_until_it_changes_what_the_database_decides()
    {
        var warmth = One(Entry("x.warmth", "Module_7E0", "Warmth"));

        var renamed = SignalWorkbench.Edited(warmth, warmth with { Name = "Oil", DefaultRateHz = 4, Max = 150 });
        Assert.Equal(warmth.Dbc, renamed.Dbc);
        Assert.False(renamed.Unconfirmed);

        var rescaled = SignalWorkbench.Edited(warmth, warmth with { Decode = warmth.Decode with { Offset = -50 } });
        Assert.Null(rescaled.Dbc);
        Assert.True(rescaled.Unconfirmed);
        Assert.Contains("\"decode\"", SignalCatalog.ToJson([rescaled]));   // the change is saved, not dropped

        Assert.Null(DbcReferences.Keep(warmth, warmth with { Decode = warmth.Decode with { Unit = "°F" } }));
    }

    [Fact]
    public void A_reference_over_a_built_in_replaces_it_like_any_correction()
    {
        var standard = SignalCatalog.FromJson("""
            [{ "id": "x.pace", "name": "Pace", "pid": 13, "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 2, "offset": 0, "unit": "km/h" } }]
            """);

        var catalog = SignalCatalog.Overlay(standard, SignalCatalog.ParseList($"[{Entry("x.pace", "Module_7E0", "Pace")}]", Library()));

        Assert.Equal(1, catalog["x.pace"].Decode.Scale);
        Assert.NotNull(catalog["x.pace"].Dbc);
    }

    [Fact]
    public void A_vehicle_file_s_references_are_followed_from_the_database_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dashdeck-{Guid.NewGuid():N}");
        var vehicles = Directory.CreateDirectory(Path.Combine(root, "vehicles")).FullName;
        var databases = Directory.CreateDirectory(Path.Combine(root, "dbc")).FullName;

        try
        {
            DbcWriter.Save(Database(), Path.Combine(databases, File));
            System.IO.File.WriteAllText(Path.Combine(vehicles, "mine.json"), $$"""
                { "name": "Mine", "match": { "make": "Example" }, "signals": [{{Entry("x.warmth", "Module_7E0", "Warmth")}}] }
                """);

            var (packs, problems) = VehiclePacks.LoadFolders(DbcLibrary.InFolder(databases), vehicles);

            Assert.Empty(problems);
            var warmth = Assert.Single(Assert.Single(packs).Signals);
            Assert.Equal((CanBus.Ms, (ushort)0xABCD), (warmth.Bus, warmth.Pid));
            Assert.Equal("°C", warmth.Decode.Unit);   // Latin-1 on disk, as Vector's tools write it
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Examples() =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TestCatalog.Path()))!, "docs", "examples");

    [Fact]
    public void The_example_database_says_what_the_standard_catalog_says()
    {
        var (db, problems) = DbcReader.Load(Path.Combine(Examples(), "obd2-standard.dbc"));
        var answers = DiagnosticAnswers.Read(db);

        Assert.Empty(problems);
        Assert.Empty(answers.Problems);
        Assert.Equal(CanBus.Hs, DiagnosticAnswers.BusOf(db));

        var asked = TestCatalog.Load().Definitions.Where(d => !d.Placeholder && d.Mode == 0x01 && d.Module is null).ToList();
        Assert.Equal(asked.Count, answers.Answers.Count);

        foreach (var d in asked)
        {
            var answer = answers.Answers.Single(a => a.Signal.Name == d.Id.Replace('.', '_'));
            Assert.Equal((d.Pid, d.Decode), (answer.Pid, answer.ToDecodeSpec()));
        }
    }

    [Fact]
    public void The_example_vehicle_file_reads_the_standard_values_through_the_database()
    {
        var (packs, problems) = VehiclePacks.LoadFolders(DbcLibrary.InFolder(Examples()), Examples());

        Assert.Empty(problems);
        var signals = Assert.Single(packs).Signals.ToDictionary(s => s.Id);
        var standard = TestCatalog.Load();

        Assert.Equal(standard["vehicle.speed"].ToRequest(), signals["dbcTest.speed"].ToRequest());
        Assert.Equal(standard["vehicle.speed"].Decode, signals["dbcTest.speed"].Decode);
        Assert.Equal(standard["engine.rpm"].Decode, signals["dbcTest.rpm"].Decode);
        Assert.Equal(standard["engine.coolantTemp"].Decode, signals["dbcTest.coolant"].Decode);

        // The one that names a signal the database lacks says so, and asks nothing.
        Assert.True(signals["dbcTest.missing"].Placeholder);
        Assert.Contains("has no signal no_such_signal", signals["dbcTest.missing"].DbcProblem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("truck.dbc", true)]
    [InlineData("my truck (2019).dbcx", true)]
    [InlineData("../truck.dbc", false)]
    [InlineData("dbc/truck.dbc", false)]
    [InlineData("C:truck.dbc", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void A_database_is_named_by_its_file_name_alone(string file, bool plain) =>
        Assert.Equal(plain, DbcLibrary.IsPlainName(file));
}
