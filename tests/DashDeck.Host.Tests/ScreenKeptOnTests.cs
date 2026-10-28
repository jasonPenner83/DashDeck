using DashDeck.Core.Screen;
using DashDeck.Host.Brightness;

namespace DashDeck.Host.Tests;

/// <summary>
/// The screen kept on while DashDeck runs and the tablet is plugged in (ADR-0080): asked only
/// when the answer changes, and let go on the way out.
/// </summary>
public sealed class ScreenKeptOnTests
{
    private PowerLine _line = PowerLine.Mains;
    private bool _granted = true;
    private readonly List<bool> _asked = [];

    [Fact]
    public void Plugged_in_asks_once_and_says_so()
    {
        var screen = Screen();

        screen.Apply();
        screen.Apply();
        screen.Apply();

        Assert.Equal([true], _asked);
        Assert.Equal("Kept on: the tablet is plugged in", screen.Status);
    }

    [Fact]
    public void Unplugging_lets_it_go_and_plugging_back_in_asks_again()
    {
        var screen = Screen();

        screen.Apply();
        _line = PowerLine.Battery;
        screen.Apply();
        _line = PowerLine.Mains;
        screen.Apply();

        Assert.Equal([true, false, true], _asked);
    }

    [Fact]
    public void Starting_on_battery_tells_windows_nothing_needs_keeping()
    {
        _line = PowerLine.Battery;
        var screen = Screen();

        screen.Apply();

        Assert.Equal([false], _asked);
        Assert.StartsWith("On battery", screen.Status);
    }

    [Fact]
    public void Closing_lets_it_go()
    {
        var screen = Screen();

        screen.Apply();
        screen.Dispose();

        Assert.Equal([true, false], _asked);
    }

    [Fact]
    public void A_refusal_is_said_rather_than_claimed()
    {
        _granted = false;
        var screen = Screen();

        screen.Apply();

        Assert.StartsWith("Windows would not keep the screen on", screen.Status);
    }

    [Fact]
    public void A_power_reading_that_throws_reads_as_unknown()
    {
        var screen = new ScreenKeptOn(() => throw new InvalidOperationException("no battery driver"), Record);

        screen.Apply();

        Assert.Equal([false], _asked);
        Assert.Contains("did not say", screen.Status);
    }

    private ScreenKeptOn Screen() => new(() => _line, Record);

    private bool Record(bool keepOn)
    {
        _asked.Add(keepOn);
        return _granted;
    }
}
