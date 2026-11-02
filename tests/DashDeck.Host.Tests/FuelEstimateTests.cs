using DashDeck.Abstractions;
using DashDeck.Host.Settings;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.Tests;

/// <summary>
/// The dash's side of the fuel estimate (ADR-0081): the correction kept in range, and a readout
/// told when economy has switched to L/h.
/// </summary>
public sealed class FuelEstimateTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.9, 0.9)]
    [InlineData(9.0, 1.5)]
    [InlineData(0.1, 0.5)]
    [InlineData(0.0, 1.0)]
    [InlineData(-2.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void The_correction_is_kept_in_range(double typed, double kept) =>
        Assert.Equal(kept, DisplaySettings.ClampCorrection(typed));

    [Fact]
    public void A_readout_is_told_the_unit_the_reading_came_in()
    {
        var source = new GaugeSource { Signal = "fuel.economy" };
        var stopped = new SignalValue("fuel.economy", 1.9, "L/h", DateTimeOffset.UnixEpoch, SignalQuality.Live);

        var reading = GaugeReading.Compute(source, stopped, null);

        Assert.Equal("L/h", reading.Unit);
        Assert.Equal(1.9, reading.Value);
    }
}
