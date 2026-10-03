using DashDeck.Host.Shell;

namespace DashDeck.Host.Tests;

/// <summary>One DashDeck at a time: what a newcomer does about one already running.</summary>
public sealed class InstanceGateTests
{
    [Fact]
    public void Nobody_else_running_starts() =>
        Assert.Equal(InstanceStep.Run, InstanceGate.Next(acquired: true, otherWindowShown: false, TimeSpan.Zero));

    /// <summary>The second tap on the icon: the dash already on screen comes forward, no second copy.</summary>
    [Fact]
    public void Another_dash_on_screen_is_brought_forward_instead() =>
        Assert.Equal(InstanceStep.ActivateOtherAndExit, InstanceGate.Next(acquired: false, otherWindowShown: true, TimeSpan.Zero));

    /// <summary>Still finding the adapter, or still closing: no window yet, so look again.</summary>
    [Fact]
    public void Another_dash_with_no_window_yet_is_waited_for() =>
        Assert.Equal(InstanceStep.Wait, InstanceGate.Next(acquired: false, otherWindowShown: false, TimeSpan.FromSeconds(5)));

    /// <summary>RESTART NOW: the old one lets go once its port is closed, and the new one starts.</summary>
    [Fact]
    public void A_closing_dash_letting_go_lets_the_newcomer_start() =>
        Assert.Equal(InstanceStep.Run, InstanceGate.Next(acquired: true, otherWindowShown: false, TimeSpan.FromSeconds(8)));

    [Fact]
    public void Waiting_too_long_gives_up_rather_than_fighting_for_the_port() =>
        Assert.Equal(InstanceStep.GiveUp, InstanceGate.Next(acquired: false, otherWindowShown: false, InstanceGate.Patience));

    /// <summary>Longer than the exit backstop, so a closing dash always gets to let go first.</summary>
    [Fact]
    public void Patience_outlasts_the_exit_backstop() =>
        Assert.True(InstanceGate.Patience > TimeSpan.FromSeconds(10));
}
