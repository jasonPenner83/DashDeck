using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Dbc;

namespace DashDeck.Tests;

/// <summary>
/// Values DashDeck asks for, held in a CAN database as the frames that answer them (ADR-0073).
/// Invented modules, identifiers and scalings throughout (ADR-0045, ADR-0052).
/// </summary>
public class DiagnosticAnswersTests
{
    private static DbcSignal Value(string name, DecodeSpec decode, byte mode = 0x22) =>
        DiagnosticAnswers.Layout(decode, mode, name) ?? throw new InvalidOperationException("no layout");

    private static DbcDocument Built()
    {
        var db = DiagnosticAnswers.WithBus(DbcDocument.Empty, CanBus.Ms);
        db = DiagnosticAnswers.Put(db, 0x7E0, 0x22, 0xABCD, Value("Warmth", new DecodeSpec(0, 1, false, 1, -40, "°C")));
        db = DiagnosticAnswers.Put(db, 0x7E0, 0x22, 0xABCE, Value("Push", new DecodeSpec(0, 2, true, 0.1, 0, "kPa")));
        db = DiagnosticAnswers.Put(db, 0x7E0, 0x01, 0x0D, Value("Pace", new DecodeSpec(0, 1, false, 1, 0, "km/h"), 0x01));
        db = DiagnosticAnswers.Put(db, 0x720, 0x22, 0x1234, Value("Lamp", new DecodeSpec(1, 1, false, 1, 0, "", 0x80)));
        return db;
    }

    [Fact]
    public void Answers_put_in_a_database_are_read_back_as_the_questions_that_get_them()
    {
        var text = DbcWriter.Write(Built());
        var (db, problems) = DbcReader.Read(text);
        var answers = DiagnosticAnswers.Read(db);

        Assert.Empty(problems);
        Assert.Empty(answers.Problems);
        Assert.Equal(
            [(0x7E0, 0x22, 0xABCD, "Warmth"), (0x7E0, 0x22, 0xABCE, "Push"), (0x7E0, 0x01, 0x0D, "Pace"), (0x720, 0x22, 0x1234, "Lamp")],
            answers.Answers.Select(a => ((int)a.Module, (int)a.Mode, (int)a.Pid, a.Signal.Name)));
        Assert.Equal(CanBus.Ms, DiagnosticAnswers.BusOf(db));
    }

    [Fact]
    public void A_module_has_one_message_multiplexed_by_service_then_identifier()
    {
        var text = DbcWriter.Write(Built());

        Assert.Contains("BO_ 2024 Module_7E0: 8 Vector__XXX\n", text);
        Assert.Contains(" SG_ Service M : 15|8@0+", text);
        Assert.Contains(" SG_ Did22 m98M : 23|16@0+", text);
        Assert.Contains(" SG_ Pid01 m65M : 23|8@0+", text);
        Assert.Contains(" SG_ Warmth m43981 : 39|8@0+ (1,-40)", text);
        Assert.Contains("SG_MUL_VAL_ 2024 Did22 Service 98-98;\n", text);
        Assert.Contains("SG_MUL_VAL_ 2024 Warmth Did22 43981-43981;\n", text);
        Assert.Contains("SG_MUL_VAL_ 2024 Pace Pid01 13-13;\n", text);
        Assert.Contains("BA_DEF_ \"DashDeckBus\" ENUM \"Hs\",\"Ms\";\n", text);
        Assert.Contains("BA_ \"DashDeckBus\" 1;\n", text);   // an enum's value is its index
    }

    [Fact]
    public void A_value_decodes_from_the_payload_the_poller_hands_over()
    {
        var answers = DiagnosticAnswers.Read(Built()).Answers;

        Assert.Equal(50, answers.Single(a => a.Signal.Name == "Warmth").Decode([90]));
        Assert.Equal(-0.2, answers.Single(a => a.Signal.Name == "Push").Decode([0xFF, 0xFE])!.Value, 9);
        Assert.Equal(88, answers.Single(a => a.Signal.Name == "Pace").Decode([88]));
        Assert.Null(answers.Single(a => a.Signal.Name == "Push").Decode([0xFF]));   // too short
    }

    [Fact]
    public void The_same_frame_decodes_the_same_through_the_database_and_the_catalog()
    {
        // The answer frame as the module sends it: length, 62, the identifier, then the value.
        byte[] frame = [0x05, 0x62, 0x12, 0x34, 0x00, 0x80, 0x00, 0x00];
        var lamp = DiagnosticAnswers.Read(Built()).Answers.Single(a => a.Signal.Name == "Lamp");

        var catalog = new DecodeSpec(1, 1, false, 1, 0, "", 0x80);
        Assert.Equal(catalog.Decode(frame.AsSpan(4)), lamp.Signal.Decode(frame));   // 128 both ways
        Assert.Equal(128, lamp.Signal.Decode(frame));
        Assert.Equal(1, lamp.Signal.Raw(frame));                                    // the field itself is one bit
    }

    public static TheoryData<DecodeSpec, byte> Decodes => new()
    {
        { new DecodeSpec(0, 1, false, 1, -40, "°C"), 0x22 },
        { new DecodeSpec(2, 2, false, 0.25, 0, "rpm"), 0x22 },
        { new DecodeSpec(0, 2, true, 0.01, 0, "A"), 0x22 },
        { new DecodeSpec(0, 4, false, 0.1, 0, "km"), 0x22 },
        { new DecodeSpec(0, 1, false, 1, 0, "", 0x80), 0x01 },
        { new DecodeSpec(0, 1, false, 1, 0, "", 0x7F), 0x01 },
        { new DecodeSpec(1, 2, false, 0.5, 0, "", 0x0FF0), 0x22 },
        { new DecodeSpec(3, 1, true, 1, 0, ""), 0x01 },
    };

    [Theory]
    [MemberData(nameof(Decodes))]
    public void A_catalog_decode_survives_the_trip_through_a_database(DecodeSpec decode, byte mode)
    {
        var signal = DiagnosticAnswers.Layout(decode, mode, "V")!;

        Assert.Equal(decode, DiagnosticAnswers.ToDecodeSpec(signal, mode));

        var random = new Random(73);
        for (var i = 0; i < 50; i++)
        {
            var payload = new byte[4];
            random.NextBytes(payload);

            var frame = new byte[DiagnosticAnswers.DataStart(mode) + payload.Length];
            payload.CopyTo(frame, DiagnosticAnswers.DataStart(mode));

            Assert.Equal(decode.Decode(payload)!.Value, signal.Decode(frame)!.Value, 9);
        }
    }

    [Fact]
    public void What_the_catalog_cannot_say_is_refused_rather_than_bent()
    {
        Assert.Null(DiagnosticAnswers.Layout(new DecodeSpec(0, 1, false, 1, 0, "", 0x5), 0x22, "Gappy"));    // not one run
        Assert.Null(DiagnosticAnswers.Layout(new DecodeSpec(0, 1, true, 1, 0, "", 0x7F), 0x22, "Signed")); // signed and masked
        Assert.Null(DiagnosticAnswers.Layout(new DecodeSpec(0, 3, false, 1, 0, ""), 0x22, "Three"));

        var intel = new DbcSignal("Intel", 32, 16, false, false, 1, 0, 0, 0, "");
        Assert.Null(DiagnosticAnswers.ToDecodeSpec(intel, 0x22));                                            // Intel across bytes

        var three = new DbcSignal("Three", 39, 24, true, false, 1, 0, 0, 0, "");
        Assert.Null(DiagnosticAnswers.ToDecodeSpec(three, 0x22));                                            // three bytes

        var early = new DbcSignal("Early", 31, 8, true, false, 1, 0, 0, 0, "");
        Assert.Null(DiagnosticAnswers.ToDecodeSpec(early, 0x22));                                            // in the identifier

        var nibble = new DbcSignal("Nibble", 36, 4, false, true, 1, 0, 0, 0, "");
        Assert.Null(DiagnosticAnswers.ToDecodeSpec(nibble, 0x22));                                           // signed, not whole bytes
    }

    [Fact]
    public void A_hand_made_file_with_one_multiplexor_on_the_identifier_is_read()
    {
        const string text = """
            BO_ 1832 Body: 8 Vector__XXX
             SG_ Did M : 23|16@0+ (1,0) [0|65535] "" Vector__XXX
             SG_ Door m4660 : 39|8@0+ (1,0) [0|1] "" Vector__XXX
             SG_ Seat m4661 : 39|8@0+ (0.5,0) [0|100] "%" Vector__XXX
            """;

        var answers = DiagnosticAnswers.Read(DbcReader.Read(text).Document);

        Assert.Empty(answers.Problems);
        Assert.Equal([(0x720, 0x22, 0x1234, "Door"), (0x720, 0x22, 0x1235, "Seat")],
            answers.Answers.Select(a => ((int)a.Module, (int)a.Mode, (int)a.Pid, a.Signal.Name)));
    }

    [Fact]
    public void A_second_multiplexor_placed_only_by_its_extended_line_is_read()
    {
        // How cantools writes the same database back: the identifier's multiplexor is a bare M, and
        // only its SG_MUL_VAL_ line says it hangs from the service at 62.
        const string text = """
            BO_ 2024 Module_7E0: 8 Vector__XXX
             SG_ Warmth m43981 : 39|8@0+ (1,-40) [0|0] "degC" Vector__XXX
             SG_ Did22 M : 23|16@0+ (1,0) [0|0] "" Vector__XXX
             SG_ Service M : 15|8@0+ (1,0) [0|0] "" Vector__XXX

            SG_MUL_VAL_ 2024 Did22 Service 98-98;
            SG_MUL_VAL_ 2024 Warmth Did22 43981-43981;
            """;

        var db = DbcReader.Read(text).Document;
        var answers = DiagnosticAnswers.Read(db);

        Assert.Empty(answers.Problems);
        Assert.Equal((0x7E0, 0x22, 0xABCD), answers.Answers.Select(a => ((int)a.Module, (int)a.Mode, (int)a.Pid)).Single());

        // And DashDeck can add to it as it stands.
        db = DiagnosticAnswers.Put(db, 0x7E0, 0x22, 0xABCE, Value("Push", new DecodeSpec(0, 2, true, 0.1, 0, "kPa")));
        Assert.Equal(2, DiagnosticAnswers.Read(db).Answers.Count);
    }

    [Fact]
    public void Answers_it_cannot_place_are_left_out_and_said()
    {
        const string text = """
            BO_ 2024 Module_7E0: 8 Vector__XXX
             SG_ Service M : 15|8@0+ (1,0) [0|0] "" Vector__XXX
             SG_ Did22 m98M : 23|16@0+ (1,0) [0|0] "" Vector__XXX
             SG_ NoLine m4660 : 39|8@0+ (1,0) [0|0] "" Vector__XXX
             SG_ Ranged m4661 : 39|8@0+ (1,0) [0|0] "" Vector__XXX
             SG_ InTheId m4662 : 31|8@0+ (1,0) [0|0] "" Vector__XXX
             SG_ Fine m4663 : 39|8@0+ (1,0) [0|0] "" Vector__XXX

            BO_ 342 NotAnAnswer: 8 Vector__XXX
             SG_ Whatever : 0|8@1+ (1,0) [0|0] "" Vector__XXX

            SG_MUL_VAL_ 2024 Did22 Service 98-98;
            SG_MUL_VAL_ 2024 Ranged Did22 4661-4670;
            SG_MUL_VAL_ 2024 InTheId Did22 4662-4662;
            SG_MUL_VAL_ 2024 Fine Did22 4663-4663;
            """;

        var answers = DiagnosticAnswers.Read(DbcReader.Read(text).Document);

        Assert.Equal(["Fine"], answers.Answers.Select(a => a.Signal.Name));
        Assert.Equal(3, answers.Problems.Count);
        Assert.Contains(answers.Problems, p => p.StartsWith("Module_7E0.NoLine: no SG_MUL_VAL_", StringComparison.Ordinal));
        Assert.Contains(answers.Problems, p => p.StartsWith("Module_7E0.Ranged: not at exactly one identifier", StringComparison.Ordinal));
        Assert.Contains(answers.Problems, p => p.StartsWith("Module_7E0.InTheId: not inside", StringComparison.Ordinal));
    }

    [Fact]
    public void Putting_a_value_again_replaces_it()
    {
        var db = Built();
        db = DiagnosticAnswers.Put(db, 0x7E0, 0x22, 0xABCD, Value("Warmth", new DecodeSpec(0, 1, false, 1, -50, "°C")));

        var warmth = DiagnosticAnswers.Read(db).Answers.Where(a => a.Signal.Name == "Warmth").ToList();
        Assert.Equal(-50, Assert.Single(warmth).Signal.Offset);
        Assert.Equal(2, db.Messages.Count);
    }

    [Fact]
    public void Put_refuses_what_it_cannot_write_honestly()
    {
        var signal = Value("V", new DecodeSpec(0, 1, false, 1, 0, ""));

        Assert.Throws<ArgumentException>(() => DiagnosticAnswers.Put(DbcDocument.Empty, 0x7E0, 0x09, 0x02, signal));
        Assert.Throws<ArgumentException>(() => DiagnosticAnswers.Put(DbcDocument.Empty, 0x7E0, 0x01, 0x100, signal));
        Assert.Throws<ArgumentException>(() => DiagnosticAnswers.Put(DbcDocument.Empty, 0x7E0, 0x22, 1, signal with { Name = "Service" }));

        // A module's message in the one-multiplexor form is the person's own; DashDeck does not rewrite it.
        var handMade = DbcReader.Read("""
            BO_ 2024 Engine: 8 Vector__XXX
             SG_ Did M : 23|16@0+ (1,0) [0|65535] "" Vector__XXX
            """).Document;
        Assert.Throws<InvalidOperationException>(() => DiagnosticAnswers.Put(handMade, 0x7E0, 0x22, 1, signal));
    }

    [Fact]
    public void A_bus_written_as_its_name_is_read_too()
    {
        var db = DbcReader.Read("""
            BA_DEF_ "DashDeckBus" ENUM "Hs","Ms";
            BA_ "DashDeckBus" "Ms";
            """).Document;

        Assert.Equal(CanBus.Ms, DiagnosticAnswers.BusOf(db));
    }

    [Fact]
    public void A_database_that_names_no_bus_says_so()
    {
        Assert.Null(DiagnosticAnswers.BusOf(DbcDocument.Empty));
        Assert.Equal(CanBus.Hs, DiagnosticAnswers.BusOf(DiagnosticAnswers.WithBus(DiagnosticAnswers.WithBus(DbcDocument.Empty, CanBus.Ms), CanBus.Hs)));
    }
}
