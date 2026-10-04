using DashDeck.Host.Stage;
using DashDeck.Host.Stage.Launcher;

namespace DashDeck.Host.Tests;

/// <summary>
/// The scroll strip beside a hosted program (ADR-0046): finger travel into wheel notches, and
/// the launcher fields that place it.
/// </summary>
/// <remarks>
/// Delivering the wheel is Win32 into a foreign process and is walked through on the tablet
/// (docs/08). What is tested here is the arithmetic that decides how much to send, and which
/// way.
/// </remarks>
public sealed class ScrollStripTests
{
    [Fact]
    public void Nothing_is_sent_until_a_whole_notch_has_built_up()
    {
        var steps = new WheelSteps(40);

        Assert.Equal(0, steps.Add(15));
        Assert.Equal(0, steps.Add(15));
        Assert.Equal(WheelSteps.Notch, steps.Add(15));
    }

    [Fact]
    public void The_remainder_is_carried_not_dropped()
    {
        var steps = new WheelSteps(40);
        var total = 0;

        for (var i = 0; i < 100; i++)
        {
            total += steps.Add(3);
        }

        // 300 px at 40 a notch is seven notches, with 20 px still carried.
        Assert.Equal(7 * WheelSteps.Notch, total);
        Assert.Equal(WheelSteps.Notch, steps.Add(20));
    }

    [Fact]
    public void Dragging_down_turns_the_wheel_up_so_the_page_follows_the_finger()
    {
        var steps = new WheelSteps(40);

        Assert.Equal(2 * WheelSteps.Notch, steps.Add(80));
        Assert.Equal(-3 * WheelSteps.Notch, steps.Add(-120));
    }

    [Fact]
    public void A_long_fast_stroke_sends_several_notches_at_once()
    {
        var steps = new WheelSteps(40);

        Assert.Equal(5 * WheelSteps.Notch, steps.Add(210));
    }

    [Fact]
    public void Reversing_cancels_travel_already_carried()
    {
        var steps = new WheelSteps(40);

        Assert.Equal(0, steps.Add(30));
        Assert.Equal(0, steps.Add(-30));
        Assert.Equal(0, steps.Add(39));
    }

    [Fact]
    public void Reset_starts_a_new_stroke_from_nothing()
    {
        var steps = new WheelSteps(40);

        steps.Add(39);
        steps.Reset();

        Assert.Equal(0, steps.Add(5));
    }

    [Fact]
    public void Nonsense_travel_sends_nothing()
    {
        var steps = new WheelSteps(40);

        Assert.Equal(0, steps.Add(double.NaN));
        Assert.Equal(0, steps.Add(double.PositiveInfinity));
        Assert.Equal(WheelSteps.Notch, steps.Add(40));
    }

    [Theory]
    [InlineData(null, null, ScrollStripSide.Right, ScrollDelivery.Message)]
    [InlineData("left", "input", ScrollStripSide.Left, ScrollDelivery.Input)]
    [InlineData(" OFF ", "Message", ScrollStripSide.Off, ScrollDelivery.Message)]
    [InlineData("middle", "telepathy", ScrollStripSide.Right, ScrollDelivery.Message)]
    public void The_launcher_fields_choose_the_side_and_the_delivery(
        string? side, string? by, ScrollStripSide expectedSide, ScrollDelivery expectedBy)
    {
        var options = ScrollStripOptions.From(side, by);

        Assert.Equal(expectedSide, options.Side);
        Assert.Equal(expectedBy, options.Delivery);
    }

    [Fact]
    public void An_app_entry_carries_its_strip_into_the_launch_spec()
    {
        var launcher = StageLauncher.Parse("""
            { "entries": [
              { "name": "NUVIO", "type": "app", "paths": [ "C:\\nuvio.exe" ], "scrollStrip": "left", "scrollBy": "input" },
              { "name": "STREMIO", "type": "app", "paths": [ "C:\\stremio.exe" ] }
            ] }
            """);

        Assert.Empty(launcher.Problems);
        Assert.Equal(
            new ScrollStripOptions(ScrollStripSide.Left, ScrollDelivery.Input),
            AppLaunchSpec.FromLauncher(launcher.Entries[0]).Scroll);
        Assert.Equal(ScrollStripOptions.Default, AppLaunchSpec.FromLauncher(launcher.Entries[1]).Scroll);
    }

    [Fact]
    public void A_bad_strip_value_falls_back_to_the_default_and_says_so()
    {
        var launcher = StageLauncher.Parse("""
            { "entries": [
              { "name": "NUVIO", "type": "app", "paths": [ "C:\\nuvio.exe" ], "scrollStrip": "top", "scrollBy": "magic" }
            ] }
            """);

        Assert.Single(launcher.Entries);
        Assert.Null(launcher.Entries[0].ScrollStrip);
        Assert.Null(launcher.Entries[0].ScrollBy);
        Assert.Contains(launcher.Problems, p => p.Contains("scrollStrip"));
        Assert.Contains(launcher.Problems, p => p.Contains("scrollBy"));
    }

    [Fact]
    public void A_user_added_app_gets_the_default_strip()
    {
        var spec = AppLaunchSpec.FromUser(new UserAppEntry { Name = "notepad", Path = @"C:\Windows\notepad.exe" });

        Assert.Equal(ScrollStripOptions.Default, spec.Scroll);
    }
}
