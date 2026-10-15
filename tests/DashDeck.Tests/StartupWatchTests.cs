using DashDeck.Core.Diagnostics;

namespace DashDeck.Tests;

/// <summary>
/// When a launch has stopped being slow and started being stuck.
/// </summary>
/// <remarks>
/// A launch on the Surface hung before its window appeared, wrote nothing, and went on holding the
/// single-instance lock — so every launch after it waited twenty seconds and exited silently too.
/// One stuck start made the dash unopenable. These pin the budget and the wording, because the
/// phase name is the whole value: "stuck" is not a diagnosis.
/// </remarks>
public sealed class StartupWatchTests
{
    [Fact]
    public void A_slow_launch_is_left_alone()
    {
        // A cold start loads a self-contained runtime, native VLC, the components and the theme
        // before drawing. Ending one of those because it took twenty seconds would be the fault.
        Assert.False(StartupWatch.ShouldGiveUp(TimeSpan.FromSeconds(20)));
        Assert.False(StartupWatch.ShouldGiveUp(TimeSpan.FromSeconds(44)));
    }

    [Fact]
    public void A_launch_past_the_budget_is_given_up_on()
    {
        Assert.True(StartupWatch.ShouldGiveUp(StartupWatch.Budget));
        Assert.True(StartupWatch.ShouldGiveUp(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_budget_leaves_room_for_a_cold_tablet()
    {
        Assert.True(StartupWatch.Budget >= TimeSpan.FromSeconds(30), "a Surface Pro 7 cold start is slow");
        Assert.True(StartupWatch.Budget <= TimeSpan.FromMinutes(2), "a minute of nothing is a fault, not patience");
    }

    [Fact]
    public void Giving_up_names_the_phase_it_never_finished()
    {
        var text = StartupWatch.Describe("preparing the adapter on COM3", TimeSpan.FromSeconds(45));

        Assert.Contains("preparing the adapter on COM3", text, StringComparison.Ordinal);
        Assert.Contains("45 s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_that_died_before_naming_a_phase_still_says_something_true()
    {
        Assert.Contains("no phase recorded", StartupWatch.Describe(null, TimeSpan.FromSeconds(45)), StringComparison.Ordinal);
        Assert.Contains("no phase recorded", StartupWatch.Describe("   ", TimeSpan.FromSeconds(45)), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_line_carries_the_elapsed_time_so_the_slow_phase_is_obvious()
    {
        var line = StartupWatch.Line("loading components", TimeSpan.FromMilliseconds(1234));

        Assert.Contains("1234 ms", line, StringComparison.Ordinal);
        Assert.Contains("loading components", line, StringComparison.Ordinal);
    }
}
