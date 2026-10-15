using DashDeck.Host.Stage;

namespace DashDeck.Host.Tests;

/// <summary>
/// When a hosted application's window should be on screen (ADR-0021). The placement arithmetic
/// needs a real window and a real display; this decision does not, and it is the part that was
/// wrong — the overflow menu opened behind Nuvio, so its own actions could not be reached.
/// </summary>
public sealed class OverlayHostTests
{
    [Theory]
    // The ordinary case: the stage is laid out, the shell is up, nothing is over it.
    [InlineData(true, false, false, true)]
    // Something the shell draws is over the stage — the menu. An owned window is above its owner,
    // so there is no z-order answer: it has to go.
    [InlineData(true, false, true, false)]
    // The anchor collapsed: the picker, a full-screen view or the warning window is up.
    [InlineData(false, false, false, false)]
    // Minimised shell. The application must not be left floating over the desktop.
    [InlineData(true, true, false, false)]
    // Any two reasons at once still means hidden, and nothing cancels out.
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    public void A_hosted_window_shows_only_when_nothing_is_in_its_way(
        bool anchorVisible,
        bool shellMinimised,
        bool obscured,
        bool expected) =>
        Assert.Equal(expected, OverlayHost.ShouldShow(anchorVisible, shellMinimised, obscured));
}
