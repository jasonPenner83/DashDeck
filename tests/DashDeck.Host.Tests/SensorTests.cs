using System.IO;
using DashDeck.Abstractions;
using DashDeck.Host.Sensors;

namespace DashDeck.Host.Tests;

/// <summary>
/// The sensor catalog, the mount reference, and the truck-first rule they exist to serve.
/// </summary>
/// <remarks>
/// None of the vehicle signals these prefer are in the signal catalog yet, so in a running
/// dash the tablet always answers. These pin the behaviour for the day one of them is found —
/// the point of the arrangement being that discovering a PID should change a JSON file and
/// nothing else (ADR-0016).
/// </remarks>
public sealed class SensorTests
{
    private static SensorDefinition Lateral => new()
    {
        Id = "motion.lateralG",
        Name = "Lateral G",
        Unit = "g",
        Source = SensorSource.Accelerometer,
        Channel = SensorChannel.Lateral,
        Prefer = "vehicle.lateralAccel",
        NeedsMountReference = true,
        Min = -2,
        Max = 2,
    };

    private static SensorCatalog CatalogOf(params SensorDefinition[] definitions) =>
        SensorCatalog.FromJson(System.Text.Json.JsonSerializer.Serialize(definitions));

    // ---- The mount reference: the arithmetic the feature stands on ----

    /// <summary>
    /// The reason the whole reference exists. A Surface on a kickstand reads 0.91 g on one
    /// axis while sitting perfectly still; rendering that raw is a third of a g of cornering
    /// force in a parked truck.
    /// </summary>
    [Fact]
    public void Gravity_is_removed_so_a_stationary_tablet_reads_zero()
    {
        var reference = Level();

        var (lateral, longitudinal) = reference.Resolve(0, -0.914, -0.406);

        Assert.Equal(0, lateral, 3);
        Assert.Equal(0, longitudinal, 3);
    }

    /// <summary>
    /// The point of building the frame from the mount rather than from the tablet: a mount at
    /// an arbitrary angle must still resolve a pure forward shove as pure longitudinal, with
    /// no lateral bleed. Assuming the tablet's axes are the truck's would fail this.
    /// </summary>
    [Fact]
    public void A_tilted_mount_still_separates_forward_from_sideways()
    {
        // Tablet pitched back 45 degrees in its cradle.
        var reference = new MountReference
        {
            Gx = 0,
            Gy = -0.7071,
            Gz = -0.7071,
            CapturedUtc = DateTimeOffset.UnixEpoch,
        };

        // A pure forward acceleration, expressed in the tablet's tilted axes.
        var (lateral, longitudinal) = reference.Resolve(0, -0.8485, -0.5657);

        Assert.Equal(0, lateral, 2);
        Assert.Equal(0.2, longitudinal, 2);
    }

    [Fact]
    public void A_sideways_shove_reads_as_lateral()
    {
        var (lateral, longitudinal) = Level().Resolve(0.3, -0.914, -0.406);

        Assert.Equal(0.3, lateral, 2);
        Assert.Equal(0, longitudinal, 2);
    }

    /// <summary>An unset reference resolves to nothing rather than to a plausible wrong number.</summary>
    [Fact]
    public void Without_a_reference_nothing_is_resolved()
    {
        var reference = new MountReference();

        Assert.False(reference.IsSet);
        Assert.Equal((0d, 0d), reference.Resolve(0.5, -0.9, -0.4));
    }

    // ---- The catalog ----

    [Fact]
    public void A_channel_the_source_cannot_produce_is_rejected_at_load()
    {
        var json = """[{ "id": "x", "name": "X", "source": "Compass", "channel": "Lateral" }]""";

        var error = Assert.Throws<InvalidDataException>(() => SensorCatalog.FromJson(json));
        Assert.Contains("cannot produce", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_sensor_id_is_rejected_at_load()
    {
        var json = """
            [{ "id": "x", "name": "X", "source": "Compass", "channel": "Heading" },
             { "id": "x", "name": "Y", "source": "Compass", "channel": "Heading" }]
            """;

        Assert.Throws<InvalidDataException>(() => SensorCatalog.FromJson(json));
    }

    /// <summary>
    /// The catalog that actually ships has to load and has to say what it claims to. This is
    /// the test that fails if somebody edits the JSON badly.
    /// </summary>
    [Fact]
    public void The_shipped_sensor_catalog_loads()
    {
        var path = CatalogPath.Find("sensors.device.json");
        Assert.NotNull(path);

        var catalog = SensorCatalog.FromFileOrEmpty(path);

        // Five tablet sensors plus the three phone-GPS entries (ADR-0027).
        Assert.Equal(8, catalog.Count);
        Assert.True(catalog.TryGet("motion.lateralG", out var lateral));
        Assert.Equal("vehicle.lateralAccel", lateral.Prefer);
        Assert.True(lateral.NeedsMountReference);

        // Heading is the one that does not need levelling: it is measured against the earth's
        // field rather than against the mount.
        Assert.True(catalog.TryGet("attitude.heading", out var heading));
        Assert.False(heading.NeedsMountReference);

        // GPS ground speed prefers the truck's own speed, so the truck wins when it is plugged in.
        Assert.True(catalog.TryGet("location.groundSpeed", out var speed));
        Assert.Equal(SensorSource.Gps, speed.Source);
        Assert.Equal("vehicle.speed", speed.Prefer);
    }

    [Fact]
    public void A_missing_sensor_catalog_is_tolerated() =>
        Assert.Equal(0, SensorCatalog.FromFileOrEmpty("nowhere/sensors.device.json").Count);

    // ---- The preference rule ----

    [Fact]
    public void The_truck_wins_when_it_has_an_answer()
    {
        var bus = new FakeBus(("vehicle.lateralAccel", 0.42, SignalQuality.Live));
        using var service = new SensorService(
            CatalogOf(Lateral), bus, Clock, new FakeDevice(0.99), new MemoryStore(Level()));

        var reading = service.Read("motion.lateralG");

        Assert.Equal(0.42, reading.Value, 3);
        Assert.Equal("TRUCK", reading.Source);
    }

    [Fact]
    public void The_tablet_answers_when_the_catalog_has_no_such_signal()
    {
        // No vehicle.lateralAccel in the catalog — which is today, and will be until a PID
        // is found. The arbiter throws on an unknown id, so this also proves it is not asked.
        var bus = new FakeBus();
        using var service = new SensorService(
            CatalogOf(Lateral), bus, Clock, new FakeDevice(0.99), new MemoryStore(Level()));

        var reading = service.Read("motion.lateralG");

        Assert.Equal(0.99, reading.Value, 3);
        Assert.Equal("TABLET", reading.Source);
        Assert.False(bus.WasAsked);
        Assert.False(service.AnyFromVehicle);
    }

    /// <summary>Preference is re-read every time, so a truck value that goes stale hands over.</summary>
    [Fact]
    public void A_stale_truck_value_hands_over_to_the_tablet()
    {
        var bus = new FakeBus(("vehicle.lateralAccel", 0.42, SignalQuality.Stale));
        using var service = new SensorService(
            CatalogOf(Lateral), bus, Clock, new FakeDevice(0.99), new MemoryStore(Level()));

        Assert.Equal("TABLET", service.Read("motion.lateralG").Source);

        bus.Set("vehicle.lateralAccel", 0.44, SignalQuality.Live);
        Assert.Equal("TRUCK", service.Read("motion.lateralG").Source);
    }

    [Fact]
    public void An_unknown_sensor_id_says_so_rather_than_throwing()
    {
        using var service = new SensorService(
            CatalogOf(Lateral), new FakeBus(), Clock, new FakeDevice(0), new MemoryStore(Level()));

        var reading = service.Read("motion.somethingElse");

        Assert.False(reading.IsUsable);
        Assert.Equal("NO SUCH SENSOR", reading.Source);
    }

    [Fact]
    public void Levelling_captures_a_reference_and_makes_readings_possible()
    {
        var store = new MemoryStore(new MountReference());

        using var service = new SensorService(
            CatalogOf(Lateral), new FakeBus(), Clock, new FakeDevice(0.5), store);

        Assert.False(service.IsLevelled);
        Assert.False(service.Read("motion.lateralG").IsUsable);

        Assert.True(service.Level());
        Assert.True(service.IsLevelled);
        Assert.True(service.Read("motion.lateralG").IsUsable);

        // Written out at once, not on exit: a dash is closed by having its power pulled, and
        // a levelling that survived only a graceful shutdown would be redone every drive.
        Assert.True(store.Load().IsSet);
    }

    // ---- The compass point ----

    [Theory]
    [InlineData(0, "N")]
    [InlineData(22, "N")]
    [InlineData(23, "NE")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(270, "W")]
    [InlineData(350, "N")]
    public void Cardinal_points_own_the_sector_centred_on_them(double degrees, string expected) =>
        Assert.Equal(expected, DashDeck.Host.Stage.Gauges.SensorMath.Cardinal(degrees));

    [Fact]
    public void An_absent_bearing_has_no_cardinal_point() =>
        Assert.Equal("——", DashDeck.Host.Stage.Gauges.SensorMath.Cardinal(double.NaN));

    private static IClock Clock => SystemClock.Instance;

    /// <summary>A tablet lying flat, screen up. Gravity along -Z.</summary>
    private static MountReference Level() => new()
    {
        Gx = 0,
        Gy = -0.914,
        Gz = -0.406,
        CapturedUtc = DateTimeOffset.UnixEpoch,
    };

    /// <summary>
    /// Keeps the reference in memory.
    /// </summary>
    /// <remarks>
    /// Added after a test levelled a fake device and <b>overwrote the real tablet's</b>
    /// mount.json, then failed on the next machine that had been levelled. A test that reads
    /// the user's state is flaky; one that writes it is worse.
    /// </remarks>
    private sealed class MemoryStore(MountReference initial) : IMountReferenceStore
    {
        private MountReference _reference = initial;

        public MountReference Load() => _reference;

        public void Save(MountReference reference) => _reference = reference;
    }

    private sealed class FakeDevice(double value) : IDeviceSensors
    {
        public bool Has(SensorDefinition definition) => true;

        public SensorReading Read(SensorDefinition definition, MountReference reference) =>
            reference.IsSet
                ? new SensorReading(value, SignalQuality.Live, "TABLET")
                : SensorReading.None("NOT LEVELLED");

        public MountReference? CaptureReference(DateTimeOffset nowUtc) => new()
        {
            Gx = 0,
            Gy = -1,
            Gz = 0,
            CapturedUtc = nowUtc,
        };

        public void Dispose()
        {
        }
    }

    /// <summary>A bus that throws when asked for a signal it does not define, like the real one.</summary>
    private sealed class FakeBus : IVehicleSignals
    {
        private readonly Dictionary<string, SignalValue> _values = new(StringComparer.Ordinal);

        public FakeBus(params (string Id, double Value, SignalQuality Quality)[] signals)
        {
            foreach (var (id, value, quality) in signals)
            {
                Set(id, value, quality);
            }
        }

        public bool WasAsked { get; private set; }

        public IReadOnlyCollection<string> KnownSignals => _values.Keys;

        public void Set(string id, double value, SignalQuality quality) =>
            _values[id] = new SignalValue(id, value, "g", DateTimeOffset.UnixEpoch, quality);

        public SignalValue Current(string signalId) =>
            _values.TryGetValue(signalId, out var value) ? value : SignalValue.Missing(signalId);

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) =>
            throw new NotSupportedException();

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz)
        {
            WasAsked = true;

            return _values.ContainsKey(signalId)
                ? new FakeDemand(signalId, rateHz)
                : throw new ArgumentException($"Signal '{signalId}' is not in the catalog.", nameof(signalId));
        }

        private sealed class FakeDemand(string signalId, double rateHz) : ISignalSubscription
        {
            public string SignalId => signalId;

            public double RequestedRateHz => rateHz;

            public double EffectiveRateHz => rateHz;

            public event Action<double>? EffectiveRateChanged
            {
                add { }
                remove { }
            }

            public void Dispose()
            {
            }
        }
    }
}
