using DashDeck.Core.Catalog;

namespace DashDeck.Tests;

public class SignalCatalogTests
{
    [Fact]
    public void Loads_the_shipped_catalog()
    {
        var catalog = TestCatalog.Load();

        Assert.True(catalog.Count >= 10);
        Assert.Contains("vehicle.speed", catalog.Ids);
        Assert.Contains("engine.rpm", catalog.Ids);
    }

    [Theory]
    // Standard OBD-II formulas, checked against known payloads.
    [InlineData("vehicle.speed", new byte[] { 0x3C }, 60)]              // A km/h
    [InlineData("engine.rpm", new byte[] { 0x1A, 0xF8 }, 1726)]         // (256A+B)/4
    [InlineData("engine.coolantTemp", new byte[] { 0x7B }, 83)]         // A-40
    [InlineData("engine.mafRate", new byte[] { 0x07, 0xD0 }, 20)]       // (256A+B)/100
    [InlineData("engine.fuelRate", new byte[] { 0x00, 0xC8 }, 10)]      // (256A+B)/20
    public void Decodes_standard_pids(string id, byte[] payload, double expected)
    {
        var value = TestCatalog.Load()[id].Decode.Decode(payload);

        Assert.NotNull(value);
        Assert.Equal(expected, value!.Value, 3);
    }

    [Fact]
    public void Returns_null_when_the_response_is_too_short()
    {
        // A truncated response must not silently decode as a small number.
        Assert.Null(TestCatalog.Load()["engine.rpm"].Decode.Decode([0x1A]));
    }

    [Fact]
    public void Rejects_duplicate_ids()
    {
        var json = """
        [
          { "id": "a", "name": "A", "pid": 1, "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "x" } },
          { "id": "a", "name": "A again", "pid": 2, "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": 0, "unit": "x" } }
        ]
        """;

        var ex = Assert.Throws<InvalidDataException>(() => SignalCatalog.FromJson(json));
        Assert.Contains("duplicate signal id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_nonsensical_byte_length()
    {
        var json = """
        [
          { "id": "a", "name": "A", "pid": 1, "decode": { "byteOffset": 0, "byteLength": 3, "signed": false, "scale": 1, "offset": 0, "unit": "x" } }
        ]
        """;

        Assert.Throws<InvalidDataException>(() => SignalCatalog.FromJson(json));
    }

    [Fact]
    public void Range_check_rejects_impossible_values()
    {
        // The guard that stops a wrong decode spec from putting a plausible-looking but
        // wrong number on the dash.
        var speed = TestCatalog.Load()["vehicle.speed"];

        Assert.True(speed.InRange(60));
        Assert.False(speed.InRange(-5));
        Assert.False(speed.InRange(900));
    }
}
