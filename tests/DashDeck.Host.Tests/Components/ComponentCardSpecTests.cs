using System.Text.Json;
using DashDeck.Host.Dash;

namespace DashDeck.Host.Tests.Components;

/// <summary>
/// A component card is a <see cref="CardSpec"/> whose source is <c>Component</c> (ADR-0023).
/// It has to survive being written and read like any other card, so the arrangement that
/// places it can be sent as a file (ADR-0015).
/// </summary>
public class ComponentCardSpecTests
{
    [Fact]
    public void A_component_source_parses()
    {
        var spec = new CardSpec { SignalId = "com.jpenner.tripcomputer", Source = "Component" };

        Assert.Equal(CardSource.Component, spec.ParsedSource);
    }

    [Fact]
    public void A_component_card_round_trips_through_json()
    {
        var original = new CardSpec
        {
            Id = "trip",
            SignalId = "com.jpenner.tripcomputer",
            Source = "Component",
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<CardSpec>(json)!;

        Assert.Equal("com.jpenner.tripcomputer", restored.SignalId);
        Assert.Equal(CardSource.Component, restored.ParsedSource);
    }

    [Fact]
    public void An_unknown_source_still_falls_back_to_signal()
    {
        // Tolerance in both directions (ADR-0015): a source a build does not recognise reads as
        // a signal rather than discarding the card.
        var spec = new CardSpec { SignalId = "vehicle.speed", Source = "SomethingNewer" };

        Assert.Equal(CardSource.Signal, spec.ParsedSource);
    }
}
