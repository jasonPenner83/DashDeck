using System.IO;
using DashDeck.Core.Catalog;
using DashDeck.Host.Dash;
using DashDeck.Host.Sensors;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.Tests;

/// <summary>How multi-state signals are shown: the console's selectors and the card picker (ADR-0056).</summary>
public sealed class MultiStateDisplayTests
{
    private static string Shipped(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", name);

    private static SignalCatalog Catalog() => SignalCatalog.FromFile(Shipped("signals.obd2-standard.json"));

    [Fact]
    public void Modern_console_shows_the_gear_and_4wd_as_selectors_of_signals_with_states()
    {
        var catalog = Catalog();
        var selectors = StageLayout.BuiltInConsole.Elements.Where(e => e.Type is StageElementType.Selector).ToList();

        Assert.Equal(["transmission.gearSelector", "drivetrain.4wdMode"], selectors.Select(s => s.Source.Signal));
        Assert.All(selectors, s => Assert.True(catalog[s.Source.Signal].HasStates, s.Id));
        Assert.Empty(StageLayout.BuiltInConsole.Problems);
    }

    [Fact]
    public void A_selector_reads_a_signal_and_knows_its_parts()
    {
        var layout = StageLayout.Parse("""
            { "name": "T", "elements": [
              { "id": "gear", "type": "selector", "x": 0, "y": 0, "width": 200, "height": 30,
                "source": { "signal": "transmission.gearSelector" }, "parts": { "litColour": "#FFFFFF", "gap": 8, "bananas": 1 } },
              { "id": "none", "type": "selector", "x": 0, "y": 40, "width": 200, "height": 30 }
            ] }
            """, null, LayoutOrigin.Yours, LayoutCanvas.Console);

        Assert.Equal(["gear"], layout.Elements.Select(e => e.Id));
        Assert.Contains(layout.Problems, p => p.StartsWith("gear:", StringComparison.Ordinal) && p.Contains("bananas", StringComparison.Ordinal));
        Assert.DoesNotContain(layout.Problems, p => p.Contains("gap", StringComparison.Ordinal));
        Assert.Contains(layout.Problems, p => p.StartsWith("none:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_card_picker_carries_the_states_and_offers_no_bar_for_them()
    {
        var choices = ValueChoice.From(Catalog()).ToDictionary(c => c.Id);

        Assert.True(choices["drivetrain.4wdMode"].HasStates);
        Assert.Equal(["2H", "4A", "4H", "4L"], choices["drivetrain.4wdMode"].States!.Select(s => s.Name));
        Assert.False(choices["vehicle.speed"].HasStates);
    }
}
