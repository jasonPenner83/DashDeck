using DashDeck.Core.Screen;

namespace DashDeck.Tests;

/// <summary>
/// Which way up the tablet is, and where the stage and the region go (ADR-0077).
/// </summary>
public sealed class ScreenShapeTests
{
    [Theory]
    [InlineData(912, 1368, true, false)]   // the tablet upright
    [InlineData(1368, 912, false, true)]   // turned on its side
    [InlineData(2736, 1824, false, true)]  // the same at 200%
    public void A_clearly_wider_or_taller_screen_decides(double width, double height, bool was, bool expected) =>
        Assert.Equal(expected, ScreenShape.IsLandscape(width, height, was));

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1000, false)]
    [InlineData(1050, 1000, false)]
    [InlineData(1000, 1050, true)]
    public void A_nearly_square_screen_keeps_what_it_was(double width, double height, bool was) =>
        Assert.Equal(was, ScreenShape.IsLandscape(width, height, was));

    [Fact]
    public void A_screen_with_no_size_yet_keeps_what_it_was()
    {
        Assert.True(ScreenShape.IsLandscape(0, 0, true));
        Assert.False(ScreenShape.IsLandscape(0, 912, false));
    }

    [Fact]
    public void Portrait_puts_the_stage_on_top_and_the_region_below()
    {
        var (stage, region) = ScreenShape.Split(landscape: false, stageShown: true, 912, 1170, 780, 912);

        Assert.Equal(new Box(0, 0, 912, 780), stage);
        Assert.Equal(new Box(0, 780, 912, 390), region);
    }

    [Fact]
    public void Landscape_puts_the_stage_at_full_width_and_the_region_beside_it()
    {
        var (stage, region) = ScreenShape.Split(landscape: true, stageShown: true, 1368, 714, 780, 912);

        Assert.Equal(new Box(0, 0, 912, 714), stage);
        Assert.Equal(new Box(912, 0, 456, 714), region);
    }

    [Theory]
    [InlineData(false, 912, 1170)]
    [InlineData(true, 1368, 714)]
    public void A_full_screen_view_takes_the_whole_body(bool landscape, double width, double height)
    {
        var (stage, region) = ScreenShape.Split(landscape, stageShown: false, width, height, 780, 912);

        Assert.Equal(new Box(0, 0, width, height), region);
        Assert.Equal(0, landscape ? stage.Width : stage.Height);
    }

    [Fact]
    public void A_body_smaller_than_the_stage_gives_it_everything_and_the_region_nothing()
    {
        var (stage, region) = ScreenShape.Split(landscape: true, stageShown: true, 800, 714, 780, 912);

        Assert.Equal(800, stage.Width);
        Assert.Equal(0, region.Width);
    }
}
