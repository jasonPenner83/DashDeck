using System.Globalization;
using System.Windows;
using DashDeck.Host;
using DashDeck.Host.Converters;

namespace DashDeck.Host.Tests;

/// <summary>
/// The band arithmetic (B2). These numbers are the design system's foundation, so it is
/// worth a test that notices if someone "tidies" one of them.
/// </summary>
public sealed class BandGridTests
{
    [Fact]
    public void The_six_bands_and_the_chrome_exactly_fill_the_screen()
    {
        var total = BandGrid.StatusStripHeight
            + BandGrid.Height(BandGrid.BandCount)
            + BandGrid.NavigationHeight;

        // 90 + 1170 + 108 = 1368. That it divides exactly is the whole reason the layout
        // was re-derived when the tablet turned out to be a Pro 7 (Q16).
        Assert.Equal(BandGrid.DesignHeight, total);
    }

    [Theory]
    [InlineData(4, 780)]  // a map wants four
    [InlineData(3, 585)]  // video and the compass want three: 16:9 at 912 wide needs 513, so it fits
    [InlineData(2, 390)]
    [InlineData(1, 195)]
    public void A_stage_of_n_bands_is_n_times_the_band_height(int bands, double expected)
    {
        Assert.Equal(expected, BandGrid.Height(bands));
    }

    [Fact]
    public void Sixteen_by_nine_video_fits_inside_three_bands()
    {
        var videoHeight = BandGrid.DesignWidth * 9 / 16;

        Assert.True(
            videoHeight <= BandGrid.Height(3),
            $"16:9 at {BandGrid.DesignWidth} wide needs {videoHeight:0}px, " +
            $"but three bands is only {BandGrid.Height(3)}px.");
    }

    [Fact]
    public void The_converter_turns_a_band_count_into_a_row_height()
    {
        var converter = new BandsToGridLengthConverter();

        var length = (GridLength)converter.Convert(3, typeof(GridLength), null, CultureInfo.InvariantCulture);

        Assert.Equal(585, length.Value);
    }
}
