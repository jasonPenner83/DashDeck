using DashDeck.Abstractions;
using DashDeck.Core.Location;

namespace DashDeck.Tests;

/// <summary>
/// What the phone's position is, built from the sentences rather than from whichever one came
/// last (ADR-0027). Every test here is a flicker that was visible in the cab, or the honest
/// degradation that must survive fixing it.
/// </summary>
public sealed class LocationTrackerTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static LocationFix Rmc(DateTimeOffset taken, double speed = 63.2, double course = 182.4) =>
        new(49.8951, -97.1384, speed, course, SignalQuality.Live, taken);

    /// <summary>A GGA: position and quality, no speed and no course. Most apps send these too.</summary>
    private static LocationFix Gga(DateTimeOffset taken) =>
        new(49.8952, -97.1385, double.NaN, double.NaN, SignalQuality.Live, taken);

    /// <summary>A void RMC or a quality-zero GGA: the receiver saying it has nothing.</summary>
    private static LocationFix NoFix(DateTimeOffset taken) =>
        new(double.NaN, double.NaN, double.NaN, double.NaN, SignalQuality.Unavailable, taken);

    [Fact]
    public void Nothing_fed_is_nothing_known()
    {
        Assert.Null(new LocationTracker().Current(At));
    }

    [Fact]
    public void A_gga_beside_an_rmc_does_not_take_the_course_away()
    {
        // The compass flicker: RMC carries a course, the GGA 200 ms later does not, so keeping
        // the newest sentence handed the heading back to the magnetometer every other second.
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.Accept(Gga(At.AddMilliseconds(200)));

        var fix = tracker.Current(At.AddMilliseconds(250))!.Value;

        Assert.Equal(182.4, fix.CourseDegrees);
        Assert.Equal(63.2, fix.GroundSpeedKmh);

        // The GGA's own position still wins — it is the newer measurement.
        Assert.Equal(49.8952, fix.Latitude);
    }

    [Fact]
    public void A_course_older_than_the_merge_window_is_not_carried_forward()
    {
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.Accept(Gga(At + LocationTracker.MergeWindow + TimeSpan.FromSeconds(1)));

        // Four seconds on, the old course is not worth claiming — a truck can have turned.
        Assert.True(double.IsNaN(tracker.Current(At.AddSeconds(4))!.Value.CourseDegrees));
    }

    [Fact]
    public void One_no_fix_sentence_does_not_throw_the_position_away()
    {
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.Accept(NoFix(At.AddSeconds(1)));

        var fix = tracker.Current(At.AddSeconds(1))!.Value;

        Assert.True(fix.HasFix);
        Assert.Equal(49.8951, fix.Latitude);
    }

    [Fact]
    public void A_receiver_that_has_never_had_a_fix_says_so_rather_than_saying_nothing()
    {
        // "No satellites" and "nothing is connected" need different answers from the person
        // holding the phone, so a no-fix sentence is reported rather than swallowed.
        var tracker = new LocationTracker();
        tracker.Accept(NoFix(At));

        var fix = tracker.Current(At)!.Value;

        Assert.False(fix.HasFix);
        Assert.Equal(SignalQuality.Unavailable, fix.Quality);
    }

    [Theory]
    [InlineData(0, SignalQuality.Live)]
    [InlineData(4, SignalQuality.Live)]
    [InlineData(7, SignalQuality.Stale)]
    [InlineData(11, SignalQuality.Unavailable)]
    public void A_held_position_ages_rather_than_staying_live_forever(int seconds, SignalQuality expected)
    {
        // Held is not frozen. This is the test that keeps the fix honest: a dot that stopped
        // being true is the worst thing a map can draw, so the hold has an end.
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));

        Assert.Equal(expected, tracker.Current(At.AddSeconds(seconds))!.Value.Quality);
    }

    [Fact]
    public void An_aged_fix_keeps_the_time_it_was_taken_so_its_age_is_on_screen()
    {
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));

        Assert.Equal(At, tracker.Current(At.AddSeconds(8))!.Value.TakenUtc);
    }

    [Fact]
    public void A_dropped_link_marks_the_fix_down_without_blanking_the_position()
    {
        // A Bluetooth hiccup that reconnects in two seconds and a phone left at home arrive here
        // the same way, so the drop costs the fix its Live standing and not its coordinates.
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.LinkLost();

        var fix = tracker.Current(At)!.Value;

        Assert.Equal(SignalQuality.Stale, fix.Quality);
        Assert.True(fix.HasFix is false);
        Assert.Equal(49.8951, fix.Latitude);
    }

    [Fact]
    public void A_link_that_stays_down_is_written_off_on_the_same_clock_as_a_quiet_one()
    {
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.LinkLost();

        Assert.Equal(SignalQuality.Stale, tracker.Current(At.AddSeconds(7))!.Value.Quality);
        Assert.Equal(SignalQuality.Unavailable, tracker.Current(At.AddSeconds(11))!.Value.Quality);
    }

    [Fact]
    public void Being_young_never_promotes_a_fix_the_link_already_marked_down()
    {
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.LinkLost();

        // Inside the hold window the ageing rule would otherwise hand back the stored quality,
        // which was Live before the drop.
        Assert.NotEqual(SignalQuality.Live, tracker.Current(At.AddSeconds(1))!.Value.Quality);
    }

    [Fact]
    public void A_reconnect_comes_back_live()
    {
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At));
        tracker.LinkLost();
        tracker.Accept(Rmc(At.AddSeconds(30)));

        Assert.Equal(SignalQuality.Live, tracker.Current(At.AddSeconds(30))!.Value.Quality);
    }

    [Fact]
    public void A_stopped_phone_keeps_its_last_course_and_the_heading_rule_handles_the_rest()
    {
        // A stationary RMC reports speed 0 and an empty course. The course carries forward, which
        // is safe because the heading is taken from the GPS only above a few km/h (ADR-0027) —
        // below that the magnetometer answers and this value is never read.
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At, speed: 63.2, course: 182.4));
        tracker.Accept(Rmc(At.AddSeconds(1), speed: 0, course: double.NaN));

        var fix = tracker.Current(At.AddSeconds(1))!.Value;

        Assert.Equal(0, fix.GroundSpeedKmh);
        Assert.Equal(182.4, fix.CourseDegrees);
        Assert.False(fix.HasUsableCourse(5));
    }

    [Fact]
    public void A_zero_speed_is_a_measurement_and_is_never_replaced_by_the_old_one()
    {
        // The carry-forward fills what is absent, not what is inconvenient: stopping must be
        // allowed to read as stopped, or the speed gauge would hold the last moving value.
        var tracker = new LocationTracker();
        tracker.Accept(Rmc(At, speed: 63.2));
        tracker.Accept(Rmc(At.AddSeconds(1), speed: 0));

        Assert.Equal(0, tracker.Current(At.AddSeconds(1))!.Value.GroundSpeedKmh);
    }
}
