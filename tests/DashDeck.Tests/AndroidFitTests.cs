using DashDeck.Core.Emulation;

namespace DashDeck.Tests;

/// <summary>Android told the shape of the region it is drawn in, so it fills it (ADR-0075 addendum).</summary>
public class AndroidFitTests
{
    // The emulator started at the stage's own size; the regions as the Surface draws them at 200 %.
    private const int DeviceWidth = 1824;
    private const int DeviceHeight = 1272;

    [Fact]
    public void On_the_stage_the_whole_screen_is_used_one_dp_to_one_dash_unit()
    {
        var fit = AndroidFit.For(DeviceWidth, DeviceHeight, turned: false, 1824, 1272, dpiScale: 2)!;

        Assert.Equal((1824, 1272, 320), (fit.Width, fit.Height, fit.Density));
        Assert.True(fit.IsWhole);
        Assert.Equal((0, 0, 1824, 1272), fit.Crop(1824, 1272));
    }

    [Fact]
    public void A_tablet_turned_upright_for_full_screen_is_told_the_full_screen_s_shape()
    {
        var fit = AndroidFit.For(DeviceWidth, DeviceHeight, turned: true, 1824, 2340, dpiScale: 2)!;

        // Upright the screen is 1272 × 1824; the tall region's shape across all of it is 1272 × 1632,
        // given to wm size unturned. The density keeps one dp the size of one of the dash's units.
        Assert.Equal((1632, 1272, 223), (fit.Width, fit.Height, fit.Density));
        Assert.False(fit.IsWhole);
        Assert.Equal((0, 96, 1272, 1632), fit.Crop(1272, 1824));
        Assert.Equal(1824.0 / 2340, fit.UprightAspect, 2);
    }

    [Fact]
    public void Automotive_is_not_turned_so_full_screen_is_a_tall_middle_of_its_wide_screen()
    {
        var fit = AndroidFit.For(DeviceWidth, DeviceHeight, turned: false, 1824, 2340, dpiScale: 2)!;

        Assert.Equal((992, 1272, 174), (fit.Width, fit.Height, fit.Density));
        Assert.Equal((416, 0, 992, 1272), fit.Crop(1824, 1272));
        Assert.Equal((208, 0, 496, 636), fit.Crop(912, 636));   // a frame scaled down is cropped in proportion
    }

    [Fact]
    public void An_avd_of_another_size_still_fits_the_region_s_shape()
    {
        // Automotive at its own 1408 × 792, joined before it was ever started at the stage's size.
        var fit = AndroidFit.For(1408, 792, turned: false, 1824, 1272, dpiScale: 2)!;

        Assert.Equal((1136, 792, 199), (fit.Width, fit.Height, fit.Density));
    }

    [Fact]
    public void A_touch_on_the_picture_is_a_touch_on_its_part_of_the_frame()
    {
        var fit = AndroidFit.For(DeviceWidth, DeviceHeight, turned: false, 1824, 2340, dpiScale: 2)!;

        Assert.Equal((0.5, 0.5), fit.ToFrame(0.5, 0.5));

        var (x, y) = fit.ToFrame(0, 1);
        Assert.Equal(416.0 / 1824, x, 4);
        Assert.Equal(1, y, 4);
    }

    [Fact]
    public void Android_is_told_with_two_shell_commands()
    {
        var fit = AndroidFit.For(DeviceWidth, DeviceHeight, turned: true, 1824, 2340, dpiScale: 2)!;

        Assert.Equal(
            [["-s", "emulator-5554", "shell", "wm", "size", "1632x1272"], ["-s", "emulator-5554", "shell", "wm", "density", "223"]],
            fit.AdbCommands("emulator-5554"));
    }

    [Fact]
    public void Nothing_is_fitted_before_the_sizes_are_known()
    {
        Assert.Null(AndroidFit.For(0, 0, turned: false, 1824, 1272, 2));
        Assert.Null(AndroidFit.For(DeviceWidth, DeviceHeight, turned: false, 0, 1272, 2));
        Assert.Null(AndroidFit.For(DeviceWidth, DeviceHeight, turned: false, 1824, 1272, 0));
    }
}
