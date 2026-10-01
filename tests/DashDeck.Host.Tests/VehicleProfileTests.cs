using DashDeck.Abstractions;
using DashDeck.Host.Components;

namespace DashDeck.Host.Tests;

/// <summary>
/// The vehicle profile and the additive contract bump it made (ADR-0029).
/// </summary>
public sealed class VehicleProfileTests
{
    [Fact]
    public void An_empty_profile_has_nothing_set() =>
        Assert.Equal(0, VehicleProfile.Empty.FuelTankLitres);

    [Theory]
    [InlineData("1.0", true)]   // the four original components still load
    [InlineData("1.1", true)]   // the profile bump
    [InlineData("1.2", true)]   // what the vehicle is, from its VIN (ADR-0033)
    [InlineData("1.3", false)]  // not from the future
    [InlineData("2.0", false)]  // a major change would need its own ADR
    public void The_host_serves_one_zero_through_the_current_minor(string apiVersion, bool served) =>
        Assert.Equal(served, ApiRange.Serves(apiVersion));
}
