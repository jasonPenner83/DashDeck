using DashDeck.Core.Audio;

namespace DashDeck.Tests;

/// <summary>
/// The audio/video sync offset — the case the measured delay actually matters for, since music
/// has nothing to be out of step with and a face on screen has everything.
/// </summary>
public class VideoSyncTests
{
    [Fact]
    public void The_starting_point_is_the_negative_of_the_link_delay()
    {
        // The direction is the whole correctness question. The link makes sound arrive LATE, so
        // the correction moves sound earlier relative to the picture. Getting the sign wrong would
        // make the problem exactly twice as bad, which is worse than doing nothing.
        var offset = VideoSync.DefaultFor(TimeSpan.FromMilliseconds(220));

        Assert.Equal(-220, offset.TotalMilliseconds);
        Assert.True(offset < TimeSpan.Zero, "a positive offset would double the error");
    }

    [Fact]
    public void An_unmeasured_link_starts_in_step()
    {
        Assert.Equal(TimeSpan.Zero, VideoSync.DefaultFor(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, 1, -50)]
    [InlineData(0, -1, 50)]
    [InlineData(-200, 1, -250)]
    [InlineData(-200, -4, 0)]
    public void A_nudge_moves_one_step_per_tap(double from, int steps, double expected) =>
        Assert.Equal(
            expected,
            VideoSync.Nudge(TimeSpan.FromMilliseconds(from), -steps).TotalMilliseconds);

    [Fact]
    public void The_offset_cannot_be_walked_past_its_limit()
    {
        // A stuck finger must not push this somewhere a person cannot find their way back from.
        var far = VideoSync.Nudge(TimeSpan.Zero, -1000);
        var other = VideoSync.Nudge(TimeSpan.Zero, 1000);

        Assert.Equal(-VideoSync.Limit, far);
        Assert.Equal(VideoSync.Limit, other);
    }

    [Fact]
    public void A_hand_edited_nonsense_value_becomes_in_step_rather_than_throwing()
    {
        // The settings file is editable by hand, and this is where a number from outside comes in.
        // Writing this test is what showed the guard was in the wrong place: a TimeSpan cannot
        // hold NaN at all — TimeSpan.FromMilliseconds(double.NaN) throws — so checking for one
        // after the conversion was unreachable code pretending to be safety. The check belongs on
        // the double, before it becomes a TimeSpan.
        Assert.Equal(TimeSpan.Zero, VideoSync.FromStored(double.NaN));
        Assert.Equal(TimeSpan.Zero, VideoSync.FromStored(double.PositiveInfinity));
        Assert.Equal(TimeSpan.Zero, VideoSync.FromStored(double.NegativeInfinity));

        // And a finite but absurd one is clamped rather than rejected, because it is probably
        // someone having a go at the file rather than a corruption.
        Assert.Equal(VideoSync.Limit, VideoSync.FromStored(999_999));
        Assert.Equal(-VideoSync.Limit, VideoSync.FromStored(-999_999));

        // An ordinary one survives untouched.
        Assert.Equal(-220, VideoSync.FromStored(-220).TotalMilliseconds);
    }

    [Fact]
    public void A_timespan_cannot_carry_a_non_finite_value_in_the_first_place()
    {
        // Pins the reason the guard moved, so nobody puts it back.
        Assert.Throws<ArgumentException>(() => TimeSpan.FromMilliseconds(double.NaN));
    }

    [Fact]
    public void LibVlc_wants_microseconds()
    {
        Assert.Equal(-220_000, VideoSync.ToMicroseconds(TimeSpan.FromMilliseconds(-220)));
        Assert.Equal(0, VideoSync.ToMicroseconds(TimeSpan.Zero));
    }

    [Fact]
    public void Microseconds_are_clamped_too_so_a_stored_value_cannot_escape_through_them()
    {
        Assert.Equal(
            (long)(-VideoSync.Limit.TotalMilliseconds * 1000),
            VideoSync.ToMicroseconds(TimeSpan.FromHours(-1)));
    }

    [Fact]
    public void The_offset_describes_itself_by_effect_not_by_sign()
    {
        // A person checking the screen can see which of the two is waiting. "-150 ms" tells them
        // nothing about which way it went.
        Assert.Equal("in step", VideoSync.Describe(TimeSpan.Zero));
        Assert.Contains("picture held back", VideoSync.Describe(TimeSpan.FromMilliseconds(-150)));
        Assert.Contains("sound held back", VideoSync.Describe(TimeSpan.FromMilliseconds(150)));
        Assert.Contains("150", VideoSync.Describe(TimeSpan.FromMilliseconds(-150)));
    }

    [Fact]
    public void A_step_is_big_enough_to_see_and_small_enough_not_to_overshoot()
    {
        // Sync errors under about 40 ms are not visible, so a finer step costs taps and buys
        // nothing; much coarser and every tap overshoots.
        Assert.InRange(VideoSync.StepMilliseconds, 40, 100);
    }
}
