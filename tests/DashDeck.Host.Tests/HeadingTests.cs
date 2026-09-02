using DashDeck.Abstractions;
using DashDeck.Host.Stage;

namespace DashDeck.Host.Tests;

/// <summary>
/// The rule the compass is built on: ask the truck first, fall back to the tablet, and say
/// which one answered.
/// </summary>
/// <remarks>
/// <c>vehicle.heading</c> is not in the catalog yet, so today the fallback always wins. These
/// pin the behaviour for the day it is — the point of the design is that finding the PID
/// should change a JSON file and nothing else.
/// </remarks>
public sealed class HeadingTests
{
    [Theory]
    [InlineData(0, "N")]
    [InlineData(22, "N")]
    [InlineData(23, "NE")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(270, "W")]
    [InlineData(350, "N")]
    [InlineData(359.9, "N")]
    public void Cardinal_points_own_the_sector_centred_on_them(double degrees, string expected) =>
        Assert.Equal(expected, new HeadingReading(degrees, SignalQuality.Live, "T").Cardinal);

    /// <summary>Due north must not read as north-east, which it does without the 22.5° bias.</summary>
    [Fact]
    public void Due_north_reads_as_north() =>
        Assert.Equal("N", new HeadingReading(0, SignalQuality.Live, "T").Cardinal);

    [Fact]
    public void An_unusable_reading_has_no_cardinal_point() =>
        Assert.Equal("——", HeadingReading.None("T").Cardinal);

    [Fact]
    public void The_truck_wins_when_it_has_an_answer()
    {
        var source = new PreferredHeadingSource(
            new StubSource("TRUCK", new HeadingReading(90, SignalQuality.Live, "TRUCK")),
            new StubSource("TABLET", new HeadingReading(270, SignalQuality.Live, "TABLET")));

        var reading = source.Read();

        Assert.Equal(90, reading.Degrees);
        Assert.Equal("TRUCK", reading.Source);
        Assert.False(source.IsFallingBack);
    }

    [Fact]
    public void The_tablet_answers_when_the_truck_cannot()
    {
        var source = new PreferredHeadingSource(
            new StubSource("TRUCK", HeadingReading.None("TRUCK"), available: false),
            new StubSource("TABLET", new HeadingReading(270, SignalQuality.Live, "TABLET")));

        var reading = source.Read();

        Assert.Equal(270, reading.Degrees);
        Assert.Equal("TABLET", reading.Source);
        Assert.True(source.IsFallingBack);
    }

    /// <summary>
    /// A truck heading that goes stale hands over rather than freezing. Preference is
    /// re-read every time, not decided once at construction.
    /// </summary>
    [Fact]
    public void A_stale_truck_heading_hands_over_to_the_tablet()
    {
        var truck = new StubSource("TRUCK", new HeadingReading(90, SignalQuality.Stale, "TRUCK"));

        var source = new PreferredHeadingSource(
            truck,
            new StubSource("TABLET", new HeadingReading(270, SignalQuality.Live, "TABLET")));

        Assert.Equal("TABLET", source.Read().Source);

        truck.Next = new HeadingReading(91, SignalQuality.Live, "TRUCK");
        Assert.Equal("TRUCK", source.Read().Source);
    }

    /// <summary>
    /// The guard that keeps the compass from taking the dash down. The arbiter throws on an
    /// unknown signal id by design, and <c>vehicle.heading</c> is deliberately not in the
    /// catalog until a real PID is found — so an unguarded Require would crash on open.
    /// </summary>
    [Fact]
    public void The_truck_source_stands_down_when_the_catalog_has_no_heading()
    {
        var bus = new CatalogWithout(TruckHeadingSource.SignalId);
        using var source = new TruckHeadingSource(bus);

        Assert.False(source.IsAvailable);
        Assert.False(source.Read().IsUsable);
        Assert.False(bus.WasAsked);
    }

    private sealed class StubSource(string name, HeadingReading reading, bool available = true)
        : IHeadingSource
    {
        public HeadingReading Next { get; set; } = reading;

        public string Name => name;

        public bool IsAvailable => available;

        public HeadingReading Read() => Next;

        public void Dispose()
        {
        }
    }

    /// <summary>A bus that would throw if asked for a signal it does not define, like the real one.</summary>
    private sealed class CatalogWithout(string absent) : IVehicleSignals
    {
        public bool WasAsked { get; private set; }

        public IReadOnlyCollection<string> KnownSignals => ["vehicle.speed", "engine.rpm"];

        public SignalValue Current(string signalId) => SignalValue.Missing(signalId);

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) =>
            throw new NotSupportedException();

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz)
        {
            WasAsked = true;
            throw new ArgumentException($"Signal '{absent}' is not in the catalog.", nameof(signalId));
        }
    }
}
