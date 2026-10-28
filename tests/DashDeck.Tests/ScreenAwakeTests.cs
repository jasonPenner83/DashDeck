using DashDeck.Core.Screen;

namespace DashDeck.Tests;

/// <summary>The screen stays on while the tablet is plugged in (ADR-0080).</summary>
public sealed class ScreenAwakeTests
{
    [Fact]
    public void Plugged_in_keeps_the_screen_on()
    {
        var decision = ScreenAwake.Decide(PowerLine.Mains);

        Assert.True(decision.KeepOn);
        Assert.Contains("plugged in", decision.Status);
    }

    [Theory]
    [InlineData(PowerLine.Battery, "On battery")]
    [InlineData(PowerLine.Unknown, "did not say")]
    public void Otherwise_windows_decides(PowerLine line, string says)
    {
        var decision = ScreenAwake.Decide(line);

        Assert.False(decision.KeepOn);
        Assert.Contains(says, decision.Status);
        Assert.Contains("as Windows is set", decision.Status);
    }
}
