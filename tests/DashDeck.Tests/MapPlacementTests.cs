using DashDeck.Core.Maps;

namespace DashDeck.Tests;

/// <summary>
/// The map's three placements, and the one rule that is easy to get wrong.
/// </summary>
/// <remarks>
/// The map is the first destination with a size of its own, so the shell's geometry gained a
/// case that nothing else has. These pin the whole rule set here, where it runs without a UI
/// thread — the shell only turns a placement into bands.
/// </remarks>
public sealed class MapPlacementTests
{
    [Fact]
    public void The_map_opens_in_the_bottom_third()
    {
        // Small first: tapping a destination should not cover the gauges before you ask it to.
        Assert.Equal(MapPlacement.Panel, MapView.Default.Placement);
        Assert.True(MapView.Default.IsPanel);
        Assert.False(MapView.Default.IsOnStage);
    }

    [Fact]
    public void The_size_button_swaps_the_two_in_nav_sizes()
    {
        var full = MapView.Default.Resized();
        Assert.Equal(MapPlacement.Full, full.Placement);
        Assert.True(full.IsFull);

        var back = full.Resized();
        Assert.Equal(MapPlacement.Panel, back.Placement);
    }

    [Fact]
    public void The_size_button_does_nothing_while_the_map_is_on_the_stage()
    {
        // The occupant has the whole picture there, so there is no second size to offer.
        var staged = MapView.Default.SentToTheStage();

        Assert.Equal(staged, staged.Resized());
    }

    [Fact]
    public void A_map_that_went_to_the_stage_full_screen_comes_back_full_screen()
    {
        // The rule this whole type exists for. Coming back as a bottom-third strip would
        // silently lose the size that was set.
        var staged = MapView.Default.Resized().SentToTheStage();

        Assert.True(staged.IsOnStage);
        Assert.Equal(MapPlacement.Full, staged.Restores);
        Assert.Equal(MapPlacement.Full, staged.BroughtBack().Placement);
    }

    [Fact]
    public void A_map_that_went_from_the_bottom_third_comes_back_to_the_bottom_third()
    {
        var staged = MapView.Default.SentToTheStage();

        Assert.Equal(MapPlacement.Panel, staged.Restores);
        Assert.Equal(MapPlacement.Panel, staged.BroughtBack().Placement);
    }

    [Fact]
    public void Being_sent_to_the_stage_twice_does_not_forget_where_it_came_from()
    {
        // Idempotent on purpose: the occupant reports its own disposal, so this arrives more
        // than once. Without it, a second send would record Stage as the size to restore.
        var once = MapView.Default.Resized().SentToTheStage();
        var twice = once.SentToTheStage();

        Assert.Equal(once, twice);
        Assert.Equal(MapPlacement.Full, twice.Restores);
    }

    [Fact]
    public void Bringing_back_a_map_that_is_already_in_the_nav_changes_nothing()
    {
        // The other half of the same reason: being replaced on the stage by something else
        // lands here too, and may land after BRING IT BACK already did.
        Assert.Equal(MapView.Default, MapView.Default.BroughtBack());

        var full = MapView.Default.Resized();
        Assert.Equal(full, full.BroughtBack());
    }

    [Fact]
    public void Restores_is_never_the_stage_itself()
    {
        // Otherwise coming back from the stage would land on the stage, and the map would be
        // unreachable from the nav.
        MapView[] states =
        [
            MapView.Default,
            MapView.Default.Resized(),
            MapView.Default.SentToTheStage(),
            MapView.Default.Resized().SentToTheStage(),
            MapView.Default.SentToTheStage().BroughtBack(),
        ];

        Assert.All(states, state => Assert.NotEqual(MapPlacement.Stage, state.Restores));
    }

    [Fact]
    public void The_caption_names_the_size_a_resize_would_give_you()
    {
        // Not the one you are already in — the oldest toggle mistake there is. The shell shows it
        // as a hint, since the control is a second press of the nav cell, but the words are here.
        Assert.Equal("FULL SCREEN", MapView.Default.ResizeCaption);
        Assert.Equal("BOTTOM THIRD", MapView.Default.Resized().ResizeCaption);
    }

    [Fact]
    public void Each_placement_says_where_it_is_for_a_screenshot()
    {
        Assert.Equal("bottom third", MapView.Default.Describe());
        Assert.Equal("full screen", MapView.Default.Resized().Describe());

        // The staged line carries the return size, because that is the part a picture cannot show.
        var staged = MapView.Default.Resized().SentToTheStage().Describe();
        Assert.Contains("on the stage", staged, StringComparison.Ordinal);
        Assert.Contains("full screen", staged, StringComparison.Ordinal);
    }
}
