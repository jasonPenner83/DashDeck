using System.Text.Json;
using DashDeck.Host.Dash;

namespace DashDeck.Host.Tests;

/// <summary>
/// Display units, and the tolerance of the stored card.
/// </summary>
/// <remarks>
/// The conversion is small enough to look obviously right, which is exactly why it is worth
/// pinning: it sits at the edge where the truck's own units become the ones on screen, and a
/// wrong constant there produces a plausible number rather than a visibly broken one.
/// </remarks>
public sealed class DisplayUnitsTests
{
    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    public void Celsius_converts_to_Fahrenheit(double celsius, double fahrenheit) =>
        Assert.Equal(fahrenheit, DisplayUnits.Convert(celsius, "°C", DisplayUnit.Fahrenheit), 3);

    [Fact]
    public void Kilometres_convert_to_miles() =>
        Assert.Equal(62.14, DisplayUnits.Convert(100, "km/h", DisplayUnit.MilesPerHour), 2);

    /// <summary>Auto is the truck's own unit, untouched. It is the default for a reason.</summary>
    [Fact]
    public void Auto_leaves_the_value_alone() =>
        Assert.Equal(100, DisplayUnits.Convert(100, "km/h", DisplayUnit.Auto));

    /// <summary>
    /// Asking for miles per hour on a temperature is a misconfiguration, not a reason to
    /// render nonsense on a windscreen.
    /// </summary>
    [Fact]
    public void A_conversion_that_does_not_apply_leaves_the_value_alone() =>
        Assert.Equal(90, DisplayUnits.Convert(90, "°C", DisplayUnit.MilesPerHour));

    [Fact]
    public void Only_signals_with_something_to_offer_get_a_unit_choice()
    {
        Assert.True(DisplayUnits.IsConvertible("°C"));
        Assert.True(DisplayUnits.IsConvertible("km/h"));
        Assert.False(DisplayUnits.IsConvertible("rpm"));
        Assert.False(DisplayUnits.IsConvertible("%"));
    }

    /// <summary>
    /// A dashboard written by an older build has to load into a newer one and back again.
    /// Adding a card setting is not a migration, and removing one is not a crash.
    /// </summary>
    [Fact]
    public void A_card_from_an_older_build_loads_with_defaults()
    {
        const string older = """{ "cards": [ { "id": "a", "signal": "vehicle.speed" } ] }""";

        var layout = JsonSerializer.Deserialize<DashboardLayout>(
            older,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var card = layout.Cards.Single();

        Assert.Equal("vehicle.speed", card.SignalId);
        Assert.Equal(1, card.Columns);
        Assert.Equal(CardStyle.Number, card.ParsedStyle);
        Assert.Equal(DisplayUnit.Auto, card.ParsedUnit);
    }

    [Fact]
    public void A_card_from_a_newer_build_loads_without_its_unknown_settings()
    {
        const string newer = """
            { "cards": [ { "id": "a", "signal": "engine.rpm", "sparkline": true, "colour": "#FF0000" } ] }
            """;

        var layout = JsonSerializer.Deserialize<DashboardLayout>(
            newer,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("engine.rpm", layout.Cards.Single().SignalId);
    }

    /// <summary>
    /// An unrecognised enum falls back rather than throwing the card away. A dashboard is
    /// not worth losing over a style this build has not heard of.
    /// </summary>
    [Fact]
    public void An_unrecognised_style_or_unit_falls_back()
    {
        var card = new CardSpec { Style = "Sparkline", Unit = "Furlongs", Priority = "Urgent" };

        Assert.Equal(CardStyle.Number, card.ParsedStyle);
        Assert.Equal(DisplayUnit.Auto, card.ParsedUnit);
        Assert.Equal(Abstractions.SignalPriority.Normal, card.ParsedPriority);
    }

    /// <summary>A stored width of seven must not break the packer.</summary>
    [Fact]
    public void A_nonsense_width_is_clamped()
    {
        Assert.Equal(BandGrid.ColumnsPerRow, new CardSpec { Width = 7 }.Columns);
        Assert.Equal(1, new CardSpec { Width = 0 }.Columns);
    }
}
