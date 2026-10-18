using DashDeck.Core.Brightness;
using Xunit;

namespace DashDeck.Tests;

/// <summary>
/// The backlight's rules (ADR-0068): which level, and when the tablet's own level goes back.
/// </summary>
public sealed class BrightnessTests
{
    [Fact]
    public void Day_and_night_take_their_own_levels()
    {
        var choice = new BrightnessChoice(true, 90, 20);

        Assert.Equal(90, BrightnessPolicy.Target(choice, isNight: false));
        Assert.Equal(20, BrightnessPolicy.Target(choice, isNight: true));
    }

    [Fact]
    public void Off_wants_nothing()
    {
        var choice = new BrightnessChoice(false, 90, 20);

        Assert.Null(BrightnessPolicy.Target(choice, isNight: false));
        Assert.Null(BrightnessPolicy.Target(choice, isNight: true));
    }

    [Theory]
    [InlineData(0, BrightnessPolicy.MinimumPercent)]
    [InlineData(-40, BrightnessPolicy.MinimumPercent)]
    [InlineData(250, BrightnessPolicy.MaximumPercent)]
    [InlineData(55, 55)]
    public void A_hand_edited_level_is_brought_into_range(int stored, int expected)
    {
        Assert.Equal(expected, BrightnessPolicy.Clamp(stored));
        Assert.Equal(expected, BrightnessPolicy.Target(new BrightnessChoice(true, stored, stored), isNight: true));
    }

    [Fact]
    public void A_black_screen_is_never_offered()
    {
        Assert.All(BrightnessPolicy.DayChoices.Concat(BrightnessPolicy.NightChoices), level =>
            Assert.InRange(level, BrightnessPolicy.MinimumPercent, BrightnessPolicy.MaximumPercent));
    }

    [Fact]
    public void The_default_is_full_by_day_and_low_by_night()
    {
        var choice = BrightnessChoice.Default;

        Assert.True(choice.Enabled);
        Assert.Equal(100, choice.DayPercent);
        Assert.True(choice.NightPercent < choice.DayPercent);
        Assert.Contains(choice.DayPercent, BrightnessPolicy.DayChoices);
        Assert.Contains(choice.NightPercent, BrightnessPolicy.NightChoices);
    }

    [Fact]
    public void Nothing_set_means_nothing_to_give_back()
    {
        var session = new BacklightSession(70, leftOver: null);

        Assert.False(session.HasChanged);
        Assert.Null(session.Record);
        Assert.Null(session.RestoreTo(70));
    }

    [Fact]
    public void The_level_found_at_launch_goes_back_on_exit()
    {
        var session = new BacklightSession(70, leftOver: null);
        session.Wrote(30);

        Assert.True(session.HasChanged);
        Assert.Equal(new BacklightLeft(70, 30), session.Record);
        Assert.Equal(70, session.RestoreTo(30));
    }

    [Fact]
    public void Setting_the_level_it_was_already_at_owes_nothing()
    {
        var session = new BacklightSession(100, leftOver: null);
        session.Wrote(100);

        Assert.False(session.HasChanged);
        Assert.Null(session.Record);
        Assert.Null(session.RestoreTo(100));
    }

    [Fact]
    public void Somebody_moving_it_since_is_not_undone()
    {
        var session = new BacklightSession(70, leftOver: null);
        session.Wrote(30);

        // The Windows slider, mid-drive.
        Assert.Null(session.RestoreTo(55));
    }

    [Fact]
    public void A_panel_that_rounds_by_a_step_is_still_the_same_level()
    {
        var session = new BacklightSession(70, leftOver: null);
        session.Wrote(30);

        Assert.Equal(70, session.RestoreTo(31));
        Assert.Equal(70, session.RestoreTo(29));
        Assert.Null(session.RestoreTo(33));
    }

    [Fact]
    public void An_unreadable_panel_on_exit_is_still_given_back()
    {
        var session = new BacklightSession(70, leftOver: null);
        session.Wrote(30);

        Assert.Equal(70, session.RestoreTo(null));
    }

    [Fact]
    public void A_crash_left_dimmed_is_recovered_at_the_next_launch()
    {
        // Last run dimmed 80 to 30 and died. This launch reads 30 — which is not the original.
        var session = new BacklightSession(30, new BacklightLeft(80, 30));

        Assert.Equal(80, session.Original);
        Assert.True(session.HasChanged);
        Assert.Equal(80, session.RestoreTo(30));
    }

    [Fact]
    public void A_crash_record_is_ignored_once_somebody_has_moved_it()
    {
        // Left at 30, but someone turned it up to 65 before launching again: their level is newer.
        var session = new BacklightSession(65, new BacklightLeft(80, 30));

        Assert.Equal(65, session.Original);
        Assert.False(session.HasChanged);
        Assert.Null(session.RestoreTo(65));
    }

    [Fact]
    public void After_a_recovered_crash_the_original_survives_new_changes()
    {
        var session = new BacklightSession(30, new BacklightLeft(80, 30));
        session.Wrote(100);

        Assert.Equal(new BacklightLeft(80, 100), session.Record);
        Assert.Equal(80, session.RestoreTo(100));
    }

    [Fact]
    public void An_unreadable_panel_at_launch_owes_nothing()
    {
        var session = new BacklightSession(null, new BacklightLeft(80, 30));

        Assert.Null(session.Original);
        Assert.False(session.HasChanged);
        Assert.Null(session.RestoreTo(null));
    }
}
