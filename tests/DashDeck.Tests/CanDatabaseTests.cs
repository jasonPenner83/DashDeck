using DashDeck.Core.Discovery.Hunt;

namespace DashDeck.Tests;

/// <summary>Reading a CAN database for the ID hunter to check (ADR-0044). Invented messages throughout.</summary>
public class CanDatabaseTests
{
    private const string Sample = """
        VERSION ""

        BU_: ENGINE BODY

        BO_ 342 EngineThing: 8 ENGINE
         SG_ OilTemp : 15|8@0+ (1,-60) [-60|193] "degC" Vector__XXX
         SG_ Pressure : 7|16@0+ (1,0) [0|65533] "kilopascal" Vector__XXX
         SG_ Torque : 32|12@1- (0.5,0) [-1024|1023.5] "Nm" Vector__XXX

        BO_ 947 BodyThing: 8 BODY
         SG_ DoorDriver : 0|1@1+ (1,0) [0|1] "SED" Vector__XXX
         SG_ Mux M : 8|2@1+ (1,0) [0|3] "" Vector__XXX

        BO_ 2364540158 Extended: 8 BODY
         SG_ Thing : 0|8@1+ (1,0) [0|255] "" Vector__XXX

        BO_ 1000 Wide_FD1: 64 BODY
         SG_ Big : 0|8@1+ (1,0) [0|255] "" Vector__XXX

        VAL_ 947 DoorDriver 1 "Ajar" 0 "Closed" ;
        """;

    [Fact]
    public void Messages_and_signals_are_read_with_their_value_tables()
    {
        var db = CanDatabase.Parse(Sample);

        Assert.Equal(4, db.Messages.Count);
        Assert.Equal(7, db.SignalCount);
        Assert.Equal(0, db.Skipped);

        var body = db.Messages.Single(m => m.Name == "BodyThing");
        Assert.Equal("3B3", body.IdText);
        Assert.Equal("Ajar", body.Signals[0].Values[1]);

        Assert.Equal(2364540158u & 0x1FFFFFFF, db.Messages.Single(m => m.Name == "Extended").Id);   // the extended flag stripped
        Assert.True(db.Messages.Single(m => m.Name == "Wide_FD1").CanFdOnly);
    }

    [Fact]
    public void Motorola_signals_read_from_their_most_significant_bit()
    {
        var engine = CanDatabase.Parse(Sample).Messages[0];
        byte[] data = [0x00, 0x96, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal(90, engine.Signals[0].Decode(data));   // byte 1: 150 − 60

        byte[] tyres = [0x00, 0xF0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(240, engine.Signals[1].Decode(tyres)); // bytes 0–1, big-endian
    }

    [Fact]
    public void Intel_signals_read_from_their_least_significant_bit_and_sign_extend()
    {
        var torque = CanDatabase.Parse(Sample).Messages[0].Signals[2];

        // 12 bits from bit 32 (byte 4), little-endian: 0xFFE is −2, × 0.5.
        byte[] data = [0, 0, 0, 0, 0xFE, 0x0F, 0, 0];
        Assert.Equal(-1, torque.Decode(data));
        Assert.Equal("start 32 len 12 LE signed", torque.Layout);
    }

    [Fact]
    public void A_value_with_text_says_it_and_a_short_frame_says_nothing()
    {
        var door = CanDatabase.Parse(Sample).Messages[1].Signals[0];

        Assert.Equal("1 (Ajar)", door.Describe([0x01]));
        Assert.Equal("—", door.Describe([]));
    }

    [Fact]
    public void Search_matches_signal_or_message_names()
    {
        var db = CanDatabase.Parse(Sample);

        Assert.Equal(["OilTemp"], db.Search("oil").Select(x => x.Signal.Name));
        Assert.Equal(["DoorDriver", "Mux"], db.Search("body").Select(x => x.Signal.Name));
        Assert.Equal(2, db.Search("door, pressure").Count);
    }
}
