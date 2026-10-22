using DashDeck.Core.Phone;

namespace DashDeck.Tests;

/// <summary>The ANDROID nav cell: tap, double tap and hold (ADR-0075, ADR-0076).</summary>
public class AndroidStageTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_tap_opens_full_screen_and_a_double_tap_sends_it_to_the_stage()
    {
        var view = AndroidView.Default;
        Assert.True(view.IsFull);

        view = view.DoubleTapped();
        Assert.True(view.IsOnStage);
        Assert.True(view.DoubleTapped().IsOnStage);   // idempotent

        // A tap on the cell brings it back off the stage, full screen.
        Assert.True(view.Tapped().IsFull);
    }

    [Fact]
    public void A_quick_press_is_a_tap_and_a_second_soon_after_is_a_double_tap()
    {
        Assert.Equal(PressKind.Tap, PressGesture.Released(Ms(0), Ms(120), previousTap: null, holdFired: false));
        Assert.Equal(PressKind.DoubleTap, PressGesture.Released(Ms(300), Ms(380), previousTap: Ms(120), holdFired: false));
        Assert.Equal(PressKind.Tap, PressGesture.Released(Ms(900), Ms(980), previousTap: Ms(120), holdFired: false));
    }

    [Fact]
    public void A_long_press_is_a_hold_and_its_lift_means_nothing_once_the_menu_is_open()
    {
        Assert.Equal(PressKind.Hold, PressGesture.Released(Ms(0), Ms(700), previousTap: null, holdFired: false));
        Assert.Null(PressGesture.Released(Ms(0), Ms(900), previousTap: null, holdFired: true));

        // A hold right after a tap is still a hold, not a double tap.
        Assert.Equal(PressKind.Hold, PressGesture.Released(Ms(200), Ms(800), previousTap: Ms(100), holdFired: false));
    }
}
