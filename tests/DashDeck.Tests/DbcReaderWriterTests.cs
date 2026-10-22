using DashDeck.Core.Dbc;

namespace DashDeck.Tests;

/// <summary>Reading and writing a CAN database (ADR-0073). Invented messages throughout (ADR-0045).</summary>
public class DbcReaderWriterTests
{
    private const string Sample = """
        VERSION "1.2"


        NS_ :
        	NS_DESC_
        	CM_
        	BA_DEF_
        	SG_MUL_VAL_

        BS_:

        BU_: ENGINE BODY
        	GATEWAY
        VAL_TABLE_ OnOff 1 "On" 0 "Off" ;


        BO_ 342 EngineThing: 8 ENGINE
         SG_ OilTemp : 15|8@0+ (1,-60) [-60|193] "°C" BODY,GATEWAY
         SG_ Torque : 32|12@1- (0.5,0) [-1024|1023.5] "Nm" Vector__XXX
         SG_ Flow : 48|16@1+ (1E-05,0) [0|0.65535] "l/s" BODY

        BO_ 2364540158 Extended: 8 BODY
         SG_ Selector M : 0|2@1+ (1,0) [0|3] "" Vector__XXX
         SG_ Sub m1M : 2|4@1+ (1,0) [0|15] "" Vector__XXX
         SG_ Deep m4 : 8|8@1+ (1,0) [0|255] "" Vector__XXX
         SG_ Plain m2 : 8|8@1+ (1,0) [0|255] "" Vector__XXX

        BO_TX_BU_ 342 : ENGINE,GATEWAY;
        EV_ Mood: 0 [0|1] "" 0 1 DUMMY_NODE_VECTOR0 Vector__XXX;

        CM_ "An invented database";
        CM_ BU_ GATEWAY "Passes things on";
        CM_ BO_ 342 "Over two
        lines, with a \"quote\"";
        CM_ SG_ 342 OilTemp "Sump";
        CM_ EV_ Mood "Not read, kept";
        CM_ BO_ 999 "A message the file does not have";
        BA_DEF_ BO_ "GenMsgCycleTime" INT -100 10000;
        BA_DEF_ "BusType" STRING ;
        BA_DEF_ SG_ "Kind" ENUM "Raw","Physical";
        BA_DEF_DEF_ "GenMsgCycleTime" 100;
        BA_DEF_DEF_ "BusType" "";
        BA_ "BusType" "CAN";
        BA_ "GenMsgCycleTime" BO_ 342 20;
        BA_ "Kind" SG_ 342 OilTemp 1;
        VAL_ 342 Torque 0 "None" -1 "Back" ;
        VAL_ Mood 0 "Calm" ;
        SG_MUL_VAL_ 2364540158 Sub Selector 1-1;
        SG_MUL_VAL_ 2364540158 Deep Sub 4-4, 6-7;
        SIG_VALTYPE_ 342 Flow : 1;
        """;

    [Fact]
    public void Everything_the_file_says_is_read()
    {
        var (db, problems) = DbcReader.Read(Sample);

        Assert.Empty(problems);
        Assert.Equal("1.2", db.Version);
        Assert.Equal(["NS_DESC_", "CM_", "BA_DEF_", "SG_MUL_VAL_"], db.NewSymbols);
        Assert.Equal(["ENGINE", "BODY", "GATEWAY"], db.Nodes);   // the list wraps
        Assert.Equal("On", db.ValueTables.Single().Values[0].Text);
        Assert.Equal("An invented database", db.Comment);
        Assert.Equal(("GATEWAY", "Passes things on"), db.NodeComments.Single());

        var engine = db.Message(342)!;
        Assert.Equal("ENGINE", engine.Sender);
        Assert.Equal("Over two\nlines, with a \"quote\"", engine.Comment!.ReplaceLineEndings("\n"));

        var oil = engine.Signal("OilTemp")!;
        Assert.Equal((15, 8, true, false, 1.0, -60.0, -60.0, 193.0, "°C"),
            (oil.StartBit, oil.Length, oil.BigEndian, oil.Signed, oil.Scale, oil.Offset, oil.Minimum, oil.Maximum, oil.Unit));
        Assert.Equal(["BODY", "GATEWAY"], oil.Receivers);
        Assert.Equal("Sump", oil.Comment);

        var torque = engine.Signal("Torque")!;
        Assert.Empty(torque.Receivers);   // Vector__XXX is nobody
        Assert.Equal("Back", torque.TextOf(-1));
        Assert.Equal(1E-05, engine.Signal("Flow")!.Scale);
    }

    [Fact]
    public void Multiplexing_is_read_with_its_extended_lines()
    {
        var extended = DbcReader.Read(Sample).Document.Messages.Single(m => m.Name == "Extended");

        Assert.True(extended.IsExtended);
        Assert.Equal(2364540158u & 0x1FFFFFFF, extended.Id);

        var selector = extended.Signal("Selector")!;
        Assert.True(selector.IsMultiplexor);
        Assert.Null(selector.MultiplexValue);

        var sub = extended.Signal("Sub")!;
        Assert.Equal("m1M", sub.MultiplexIndicator);
        Assert.Equal("Selector", sub.Switch!.Multiplexor);
        Assert.Equal([new DbcMultiplexRange(1, 1)], sub.Switch.Ranges);

        var deep = extended.Signal("Deep")!;
        Assert.Equal("Sub", deep.Switch!.Multiplexor);
        Assert.Equal([new DbcMultiplexRange(4, 4), new DbcMultiplexRange(6, 7)], deep.Switch.Ranges);

        Assert.Null(extended.Signal("Plain")!.Switch);   // a plain m2 hangs from the only top-level multiplexor
    }

    [Fact]
    public void Attributes_are_read_with_their_types_and_targets()
    {
        var db = DbcReader.Read(Sample).Document;

        var cycle = db.AttributeDefinitions.Single(d => d.Name == "GenMsgCycleTime");
        Assert.Equal((DbcObjectKind.Message, "INT"), (cycle.Kind, cycle.Type));
        Assert.Equal(["-100", "10000"], cycle.Arguments.Select(a => a.Text));   // a minus after a space is a number

        Assert.Equal(["Raw", "Physical"], db.AttributeDefinitions.Single(d => d.Name == "Kind").Arguments.Select(a => a.Text));
        Assert.Equal(new DbcToken("CAN", true), db.NetworkAttribute("BusType"));
        Assert.Equal(new DbcAttributeValue("GenMsgCycleTime", DbcObjectKind.Message, null, 342, new DbcToken("20", false)),
            db.AttributeValues.Single(a => a.Kind == DbcObjectKind.Message));
        Assert.Equal(("OilTemp", 342u), db.AttributeValues.Where(a => a.Kind == DbcObjectKind.Signal).Select(a => (a.Target!, a.RawMessageId!.Value)).Single());
        Assert.Equal(new DbcToken("100", false), db.AttributeDefaults[0].Value);
    }

    [Fact]
    public void What_is_not_read_is_kept_word_for_word()
    {
        var verbatim = DbcReader.Read(Sample).Document.Verbatim;

        Assert.Equal(
            [
                "BO_TX_BU_ 342 : ENGINE,GATEWAY;",
                "EV_ Mood: 0 [0|1] \"\" 0 1 DUMMY_NODE_VECTOR0 Vector__XXX;",
                "CM_ EV_ Mood \"Not read, kept\";",
                "CM_ BO_ 999 \"A message the file does not have\";",
                "VAL_ Mood 0 \"Calm\" ;",
                "SIG_VALTYPE_ 342 Flow : 1;",
            ],
            verbatim.Select(v => v.Text));
    }

    [Fact]
    public void Writing_what_was_read_and_reading_it_again_changes_nothing()
    {
        var once = DbcWriter.Write(DbcReader.Read(Sample).Document);
        var (again, problems) = DbcReader.Read(once);

        Assert.Empty(problems);
        Assert.Equal(once, DbcWriter.Write(again));

        // And nothing was lost on the way: the same counts of everything.
        var first = DbcReader.Read(Sample).Document;
        Assert.Equal(first.Messages.Sum(m => m.Signals.Count), again.Messages.Sum(m => m.Signals.Count));
        Assert.Equal(first.AttributeValues.Count, again.AttributeValues.Count);
        Assert.Equal(first.Verbatim.Select(v => v.Text), again.Verbatim.Select(v => v.Text));
        Assert.Equal(first.Messages.SelectMany(m => m.Signals).Select(s => (s.Name, s.Comment, s.Switch?.Multiplexor, s.Values.Count)),
            again.Messages.SelectMany(m => m.Signals).Select(s => (s.Name, s.Comment, s.Switch?.Multiplexor, s.Values.Count)));
    }

    [Fact]
    public void A_signal_is_written_the_way_tools_write_it()
    {
        var text = DbcWriter.Write(DbcReader.Read(Sample).Document);

        Assert.Contains(" SG_ OilTemp : 15|8@0+ (1,-60) [-60|193] \"°C\" BODY,GATEWAY\n", text);
        Assert.Contains(" SG_ Sub m1M : 2|4@1+ (1,0) [0|15] \"\" Vector__XXX\n", text);
        Assert.Contains(" SG_ Flow : 48|16@1+ (1E-05,0) [0|0.65535] \"l/s\" BODY\n", text);
        Assert.Contains("BO_ 2364540158 Extended: 8 BODY\n", text);
        Assert.Contains("SG_MUL_VAL_ 2364540158 Deep Sub 4-4, 6-7;\n", text);
        Assert.Contains("CM_ BO_ 342 \"Over two", text);
        Assert.Contains("with a \\\"quote\\\"\";\n", text);
        Assert.Contains("BA_DEF_ SG_ \"Kind\" ENUM \"Raw\",\"Physical\";\n", text);
    }

    [Fact]
    public void A_statement_it_cannot_read_is_reported_and_the_rest_still_read()
    {
        const string text = """
            BU_: ENGINE

            BO_ 100 First: 8 ENGINE
             SG_ Good : 0|8@1+ (1,0) [0|255] "" Vector__XXX
             SG_ Broken : 8|8@2+ (1,0) [0|255] "" Vector__XXX
             SG_ AlsoGood : 16|8@1+ (1,0) [0|255] "" Vector__XXX

            BO_ 200 Second: 8 ENGINE
             SG_ Last : 0|8@1+ (2,0) [0|510] "" Vector__XXX
            """;

        var (db, problems) = DbcReader.Read(text);

        Assert.Equal(["Good", "AlsoGood"], db.Message(100)!.Signals.Select(s => s.Name));
        Assert.Equal(2, db.Message(200)!.Signals.Single().Scale);
        Assert.StartsWith("line 5: SG_", Assert.Single(problems));
    }

    [Fact]
    public void A_file_saved_and_loaded_keeps_its_degree_signs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dashdeck-{Guid.NewGuid():N}.dbc");

        try
        {
            DbcWriter.Save(DbcReader.Read(Sample).Document, path);
            Assert.Contains("\r\n", File.ReadAllText(path));

            var (db, problems) = DbcReader.Load(path);
            Assert.Empty(problems);
            Assert.Equal("°C", db.Message(342)!.Signal("OilTemp")!.Unit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_empty_database_writes_a_file_that_reads_back_empty()
    {
        var text = DbcWriter.Write(DbcDocument.Empty);
        var (db, problems) = DbcReader.Read(text);

        Assert.Empty(problems);
        Assert.Empty(db.Messages);
        Assert.Equal(DbcDocument.StandardNewSymbols, db.NewSymbols);
    }

    [Fact]
    public void Bits_read_in_either_byte_order()
    {
        byte[] data = [0x12, 0x34, 0x56, 0x78];

        Assert.Equal(0x1234, DbcBits.Raw(data, 7, 16, bigEndian: true, signed: false));    // Motorola from byte 0's top bit
        Assert.Equal(0x3412, DbcBits.Raw(data, 0, 16, bigEndian: false, signed: false));   // Intel from byte 0's bottom bit
        Assert.Equal(0x3, DbcBits.Raw(data, 12, 4, bigEndian: false, signed: false));      // byte 1's top nibble
        Assert.Equal(-1, DbcBits.Raw([0xFF], 0, 8, bigEndian: false, signed: true));
        Assert.Null(DbcBits.Raw(data, 31, 16, bigEndian: true, signed: false));            // runs off the end
    }
}
