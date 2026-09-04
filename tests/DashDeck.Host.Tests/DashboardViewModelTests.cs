using DashDeck.Abstractions;
using DashDeck.Host.Dash;
using DashDeck.Host.Sensors;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// The rule that makes "a bunch of widget cards" safe to offer: only the page you are
/// looking at declares signals.
/// </summary>
/// <remarks>
/// Every card is demand on one serialised link whose real ceiling is still unmeasured (Q12),
/// and the arbiter divides one budget across everything declared (ADR-0004). Without this,
/// twenty cards spread over four pages would degrade the six in front of you to pay for
/// fourteen nobody can see.
/// <para>
/// The status strip cannot be used to check this by eye, incidentally â€” its req/s figure is
/// the adapter's measured <em>capability</em>, not the load, so it barely moves either way.
/// That is exactly why this is a test.
/// </para>
/// </remarks>
public sealed class DashboardViewModelTests
{
    // Eighteen distinct ids, because one page now holds fifteen (5 × 3) and the paging tests
    // need more than a page to have a second one to turn to. The strings are arbitrary — the
    // bus and choices here are fakes — but read as real signals for legibility.
    private static readonly string[] Ids =
    [
        "vehicle.speed", "engine.rpm", "engine.coolantTemp",
        "fuel.levelPercent", "engine.load", "engine.fuelRate",
        "engine.throttlePosition", "engine.mafRate", "engine.intakeAirTemp",
        "engine.oilTemp", "engine.intakeManifoldPressure", "engine.barometricPressure",
        "vehicle.controlModuleVoltage", "engine.timingAdvance", "engine.commandedEquivRatio",
        "engine.ambientAirTemp", "engine.runTime", "vehicle.distanceSinceCodesCleared",
    ];

    private static IReadOnlyList<ValueChoice> Choices() =>
        [.. Ids.Select(id => new ValueChoice(id, id, "%", 1, 0, 100, CardSource.Signal, "Test"))];

    private static DashboardLayout LayoutOf(int cards) => new()
    {
        Cards = [.. Ids.Take(cards).Select(id => new CardSpec { Id = id, SignalId = id })],
    };

    /// <summary>
    /// A factory over the fake bus and an empty sensor catalog.
    /// </summary>
    /// <remarks>
    /// Empty rather than absent: these tests are about signal cards and the budget they spend,
    /// and a sensor catalog with nothing in it keeps that the only variable.
    /// </remarks>
    private static CardValueFactory Factory(FakeBus bus) => new(
        bus,
        new SensorService(SensorCatalog.Empty, bus, SystemClock.Instance, new NoDevice(), new NoMount()));

    private static DashboardViewModel Build(FakeBus bus, MemoryStore store, int bands = 2)
    {
        var dashboard = new DashboardViewModel(Factory(bus), Choices(), store: store);
        dashboard.WidgetBands = bands;
        return dashboard;
    }

    [Fact]
    public void Only_the_visible_page_declares_signals()
    {
        var bus = new FakeBus();
        using var dashboard = Build(bus, new MemoryStore(LayoutOf(18)));

        // Eighteen cards is two pages behind a four-band stage: fifteen visible (5 × 3), three not.
        Assert.Equal(2, dashboard.Pages.Count);
        Assert.Equal(15, bus.LiveDemands);
    }

    [Fact]
    public void Turning_the_page_moves_the_declarations_rather_than_adding_to_them()
    {
        var bus = new FakeBus();
        using var dashboard = Build(bus, new MemoryStore(LayoutOf(18)));

        dashboard.GoToPageCommand.Execute(1);

        // Three cards on the second page, and — the point of the test — the fifteen from the
        // first page have gone rather than lingering.
        Assert.Equal(3, bus.LiveDemands);
        Assert.DoesNotContain("vehicle.speed", bus.Declared);
        Assert.Contains("engine.runTime", bus.Declared);
    }

    [Fact]
    public void A_taller_widget_region_fits_more_cards_and_so_declares_more()
    {
        var bus = new FakeBus();
        using var dashboard = Build(bus, new MemoryStore(LayoutOf(18)), bands: 3);

        // A three-band stage leaves four rows — twenty slots at five across — so all eighteen
        // fit one page and declare, where the two-band dash pages them (fifteen, then three).
        Assert.Single(dashboard.Pages);
        Assert.Equal(18, bus.LiveDemands);
    }

    [Fact]
    public void A_card_bound_to_a_signal_the_catalog_lost_is_kept_and_marked_rather_than_dropped()
    {
        var bus = new FakeBus();
        var layout = new DashboardLayout
        {
            Cards = [new CardSpec { Id = "x", SignalId = "engine.transmissionTempThatNeverWas" }],
        };

        using var dashboard = Build(bus, new MemoryStore(layout));

        var card = dashboard.Pages[0].Slots.OfType<WidgetCardViewModel>().Single();

        Assert.True(card.IsMissing);
        Assert.Equal(SignalQuality.Unavailable, card.Quality);

        // And crucially it is never declared: the arbiter throws on an unknown id, by design.
        Assert.Equal(0, bus.LiveDemands);
    }

    [Fact]
    public void Removing_every_card_is_a_choice_that_survives_a_restart()
    {
        var bus = new FakeBus();
        var store = new MemoryStore(LayoutOf(1));

        using (var dashboard = Build(bus, store))
        {
            var card = dashboard.Pages[0].Slots.OfType<WidgetCardViewModel>().Single();
            dashboard.RemoveCardCommand.Execute(card);
        }

        Assert.NotNull(store.Saved);
        Assert.Empty(store.Saved!.Cards);

        // Reloaded, an empty dashboard stays empty rather than being helpfully refilled.
        using var reopened = Build(new FakeBus(), store);
        Assert.True(reopened.IsEmpty);
    }

    [Fact]
    public void Editing_a_card_writes_the_arrangement_out_at_once()
    {
        var bus = new FakeBus();
        var store = new MemoryStore(LayoutOf(1));
        using var dashboard = Build(bus, store);

        var card = dashboard.Pages[0].Slots.OfType<WidgetCardViewModel>().Single();
        dashboard.CardEdited(card, card.Spec with { Label = "MY SPEED", Width = 2 });

        Assert.Equal("MY SPEED", store.Saved!.Cards[0].Label);
        Assert.Equal(2, store.Saved.Cards[0].Width);
        Assert.Equal("MY SPEED", card.Label);
    }

    /// <summary>
    /// Changing only how a card looks must not disturb the polling plan. Re-declaring on
    /// every keystroke in the label box would re-plan the whole link for a cosmetic edit.
    /// </summary>
    [Fact]
    public void A_presentation_only_edit_does_not_redeclare_the_signal()
    {
        var bus = new FakeBus();
        using var dashboard = Build(bus, new MemoryStore(LayoutOf(1)));

        var card = dashboard.Pages[0].Slots.OfType<WidgetCardViewModel>().Single();
        var before = bus.TotalDeclarations;

        dashboard.CardEdited(card, card.Spec with { Label = "RENAMED", Unit = "Fahrenheit" });

        Assert.Equal(before, bus.TotalDeclarations);
    }

    /// <summary>
    /// Found by using it: a shipped card labelled RPM was pointed at coolant and kept saying
    /// RPM above a temperature. A label naming the wrong quantity is worse than one that has
    /// to be retyped, and an empty label is never nameless â€” it falls back to the signal.
    /// </summary>
    [Fact]
    public void Changing_a_card_signal_drops_a_label_that_named_the_old_one()
    {
        var bus = new FakeBus();
        var store = new MemoryStore(new DashboardLayout
        {
            Cards = [new CardSpec { Id = "a", SignalId = "engine.rpm", Label = "RPM" }],
        });

        using var dashboard = Build(bus, store);
        var card = dashboard.Pages[0].Slots.OfType<WidgetCardViewModel>().Single();

        var editor = new CardEditorViewModel(dashboard, card, Choices());
        editor.ChooseCommand.Execute(
            editor.Signals.Single(c => ((ValueChoice)c.Value).Id == "engine.coolantTemp"));

        Assert.Equal("engine.coolantTemp", card.Spec.SignalId);
        Assert.Empty(card.Spec.Label);
        Assert.DoesNotContain("RPM", card.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_the_rate_does_redeclare_it()
    {
        var bus = new FakeBus();
        using var dashboard = Build(bus, new MemoryStore(LayoutOf(1)));

        var card = dashboard.Pages[0].Slots.OfType<WidgetCardViewModel>().Single();
        var before = bus.TotalDeclarations;

        dashboard.CardEdited(card, card.Spec with { RateHz = 4 });

        Assert.True(bus.TotalDeclarations > before);
    }

    private sealed class MemoryStore(DashboardLayout? initial) : IDashboardStore
    {
        public DashboardLayout? Saved { get; private set; }

        public DashboardLayout? Load() => Saved ?? initial;

        public void Save(DashboardLayout layout) => Saved = layout;
    }

    /// <summary>A bus that counts what is declared, which is the whole subject here.</summary>
    private sealed class FakeBus : IVehicleSignals
    {
        private readonly List<string> _declared = [];

        public IReadOnlyCollection<string> Declared => _declared;

        public int LiveDemands => _declared.Count;

        /// <summary>Every declaration ever made, so a needless re-declare is visible.</summary>
        public int TotalDeclarations { get; private set; }

        public IReadOnlyCollection<string> KnownSignals => Ids;

        public SignalValue Current(string signalId) => SignalValue.Missing(signalId, "%");

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) =>
            new Disposer(() => { });

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz)
        {
            _declared.Add(signalId);
            TotalDeclarations++;
            return new FakeDemand(signalId, rateHz, () => _declared.Remove(signalId));
        }

        private sealed class FakeDemand(string signalId, double rateHz, Action onDispose)
            : ISignalSubscription
        {
            public string SignalId => signalId;

            public double RequestedRateHz => rateHz;

            public double EffectiveRateHz => rateHz;

            public event Action<double>? EffectiveRateChanged
            {
                add { }
                remove { }
            }

            public void Dispose() => onDispose();
        }

        private sealed class Disposer(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}


/// <summary>Stand-ins so a dashboard test never touches real hardware or the real mount file.</summary>
file sealed class NoDevice : IDeviceSensors
{
    public bool Has(SensorDefinition definition) => false;

    public SensorReading Read(SensorDefinition definition, MountReference reference) =>
        SensorReading.None("NO SENSOR");

    public MountReference? CaptureReference(DateTimeOffset nowUtc) => null;

    public void Dispose()
    {
    }
}

file sealed class NoMount : IMountReferenceStore
{
    public MountReference Load() => new();

    public void Save(MountReference reference)
    {
    }
}

