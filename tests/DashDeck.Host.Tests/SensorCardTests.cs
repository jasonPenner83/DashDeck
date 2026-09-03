using DashDeck.Abstractions;
using DashDeck.Host.Dash;
using DashDeck.Host.Sensors;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// Cards bound to tablet sensors (B4).
/// </summary>
/// <remarks>
/// The gap this closes: five values — a heading, pitch, roll and two axes of G — were
/// catalogued, range-guarded and quality-tagged, and no card could bind to any of them. The
/// interesting behaviour is that they cost no request budget and that they say where they
/// came from rather than reporting a rate nothing allocated.
/// </remarks>
public sealed class SensorCardTests
{
    private const string SensorJson = """
        [
          { "id": "attitude.pitch", "name": "Pitch", "unit": "°", "source": "Inclinometer",
            "channel": "Pitch", "prefer": "vehicle.pitch", "needsMountReference": true,
            "min": -45, "max": 45 }
        ]
        """;

    private static SensorCatalog Sensors() => SensorCatalog.FromJson(SensorJson);

    private static (CardValueFactory Factory, IReadOnlyList<ValueChoice> Choices, StubDevice Device) Build()
    {
        var bus = new StubBus();
        var device = new StubDevice();
        var sensors = new SensorService(Sensors(), bus, SystemClock.Instance, device, new StubMount());

        return (
            new CardValueFactory(bus, sensors),
            ValueChoice.All(EmptySignalCatalog(), Sensors()),
            device);
    }

    /// <summary>An empty signal catalog, so only the sensor half is in play.</summary>
    private static DashDeck.Core.Catalog.SignalCatalog EmptySignalCatalog() =>
        DashDeck.Core.Catalog.SignalCatalog.FromJson("[]");

    [Fact]
    public void A_sensor_appears_in_the_choices_and_is_marked_as_one()
    {
        var (_, choices, _) = Build();

        var pitch = Assert.Single(choices);

        Assert.Equal("attitude.pitch", pitch.Id);
        Assert.Equal(CardSource.Sensor, pitch.Source);
        Assert.Contains("sensor", pitch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_can_be_bound_to_a_sensor_and_shows_its_value()
    {
        var (factory, choices, device) = Build();
        device.Value = 12.5;

        var spec = new CardSpec
        {
            Id = "a",
            SignalId = "attitude.pitch",
            Source = nameof(CardSource.Sensor),
            Format = "0.0",
        };

        using var card = new WidgetCardViewModel(factory, spec, choices[0]);
        card.Activate();

        Assert.False(card.IsMissing);
        Assert.Equal("12.5", card.Text);
        Assert.Equal("°", card.UnitText);
        Assert.Equal(SignalQuality.Live, card.Quality);
    }

    /// <summary>
    /// The point of a sensor card. Nothing is asked of the truck, so nothing may be declared —
    /// reading a magnetometer is not traffic on the OBD-II link (ADR-0017).
    /// </summary>
    [Fact]
    public void A_sensor_card_costs_no_request_budget()
    {
        var bus = new StubBus();
        var sensors = new SensorService(Sensors(), bus, SystemClock.Instance, new StubDevice(), new StubMount());
        var factory = new CardValueFactory(bus, sensors);

        var spec = new CardSpec { Id = "a", SignalId = "attitude.pitch", Source = "Sensor" };

        using var card = new WidgetCardViewModel(factory, spec, ValueChoice.From(Sensors()).Single());
        card.Activate();

        Assert.True(card.IsActive);
        Assert.Equal(0, bus.Declarations);
    }

    /// <summary>
    /// A rate would be an invented cost. The source is the thing worth knowing, and it is
    /// what tells you whether the truck answered or the tablet guessed.
    /// </summary>
    [Fact]
    public void A_sensor_card_reports_its_source_rather_than_an_allocated_rate()
    {
        var (factory, choices, device) = Build();
        device.Source = "TABLET";

        var spec = new CardSpec { Id = "a", SignalId = "attitude.pitch", Source = "Sensor" };

        using var card = new WidgetCardViewModel(factory, spec, choices[0]);
        card.Activate();

        Assert.Equal("TABLET", card.FooterText);
        Assert.DoesNotContain("Hz", card.FooterText, StringComparison.Ordinal);
    }

    /// <summary>A dashboard written before sensors existed still means signals.</summary>
    [Fact]
    public void A_card_without_a_source_defaults_to_a_signal()
    {
        var spec = System.Text.Json.JsonSerializer.Deserialize<CardSpec>(
            """{ "id": "a", "signal": "vehicle.speed" }""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(spec);
        Assert.Equal(CardSource.Signal, spec.ParsedSource);
    }

    /// <summary>
    /// Ids are no longer unique across the two catalogs — the sensor catalog names
    /// <c>vehicle.pitch</c> as the signal it would prefer — so a lookup must match the source
    /// too or a card silently binds to the wrong catalog's entry.
    /// </summary>
    [Fact]
    public void A_shared_id_resolves_by_source_rather_than_by_name()
    {
        var choices = new List<ValueChoice>
        {
            new("vehicle.pitch", "Pitch", "°", 4, -45, 45, CardSource.Signal),
            new("vehicle.pitch", "Pitch", "°", 4, -45, 45, CardSource.Sensor),
        };

        var asSensor = choices.Single(c =>
            c.Source == CardSource.Sensor && c.Id == "vehicle.pitch");

        Assert.Equal(CardSource.Sensor, asSensor.Source);
    }

    private sealed class StubDevice : IDeviceSensors
    {
        public double Value { get; set; } = 1.0;

        public string Source { get; set; } = "TABLET";

        public bool Has(SensorDefinition definition) => true;

        public SensorReading Read(SensorDefinition definition, MountReference reference) =>
            new(Value, SignalQuality.Live, Source);

        public MountReference? CaptureReference(DateTimeOffset nowUtc) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubMount : IMountReferenceStore
    {
        public MountReference Load() => new()
        {
            Gx = 0,
            Gy = -1,
            Gz = 0,
            CapturedUtc = DateTimeOffset.UnixEpoch,
        };

        public void Save(MountReference reference)
        {
        }
    }

    /// <summary>Counts declarations, so "costs nothing" can be asserted rather than assumed.</summary>
    private sealed class StubBus : IVehicleSignals
    {
        public int Declarations { get; private set; }

        public IReadOnlyCollection<string> KnownSignals => [];

        public SignalValue Current(string signalId) => SignalValue.Missing(signalId);

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) =>
            throw new NotSupportedException();

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz)
        {
            Declarations++;
            throw new ArgumentException($"Signal '{signalId}' is not in the catalog.", nameof(signalId));
        }
    }
}
