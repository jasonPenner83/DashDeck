using DashDeck.Host.Shell;

namespace DashDeck.Host.Tests;

/// <summary>CLOSE DASHDECK takes two taps, because on the truck it is the only way out.</summary>
public sealed class CloseConfirmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_tap_arms_and_the_second_closes()
    {
        var close = new CloseConfirm(TimeSpan.FromSeconds(4));

        Assert.False(close.Tap(T0));
        Assert.True(close.IsArmed(T0.AddSeconds(1)));
        Assert.True(close.Tap(T0.AddSeconds(2)));
        Assert.False(close.IsArmed(T0.AddSeconds(2)));
    }

    [Fact]
    public void Left_alone_it_goes_back_to_harmless()
    {
        var close = new CloseConfirm(TimeSpan.FromSeconds(4));

        close.Tap(T0);

        Assert.False(close.IsArmed(T0.AddSeconds(4)));
        Assert.False(close.Tap(T0.AddSeconds(5)));
    }

    [Fact]
    public void Dismissing_the_menu_disarms_it()
    {
        var close = new CloseConfirm(TimeSpan.FromSeconds(4));

        close.Tap(T0);
        close.Disarm();

        Assert.False(close.Tap(T0.AddSeconds(1)));
    }
}
