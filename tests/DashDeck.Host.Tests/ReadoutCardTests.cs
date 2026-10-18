using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Maps;
using DashDeck.Host.Dash;
using DashDeck.Host.Sensors;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// Words on a card (ADR-0067): the third source beside a vehicle signal and a tablet sensor.
/// </summary>
/// <remarks>
/// The thing worth testing is the boundary, not the string handling. A card value's contract is a
/// <c>double</c> and NaN means "nothing to draw", so a readout — which has no number at all —
/// would render as a dash through every guard in the dash unless exactly one place asks which kind
/// it has. These pin that place, and pin that nothing else learned about it.
/// </remarks>
public sealed class ReadoutCardTests
{
    private static GeocodeResult Shop => new(
        "Tim Hortons",
        "Portage Avenue, Winnipeg",
        49.8951,
        -97.1384,
        new PlaceDetails("Fast food", "https://example.test", "+1 204 555 0100", "Mo-Su 06:00-22:00"));

    private static Route SomeRoute => new(12_400, 1080, [(-97.14, 49.89), (-97.13, 49.90)], []);

    private static ReadoutCatalog Catalog(GeocodeResult? place = null, Route? route = null) =>
        ReadoutCatalog.ForNavigation(() => place, () => route);

    // ---- The catalog ----

    [Fact]
    public void The_navigation_readouts_are_the_things_a_place_knows_that_are_not_numbers()
    {
        var catalog = Catalog(Shop, SomeRoute);

        Assert.Equal(7, catalog.Count);
        Assert.All(catalog.Definitions, d => Assert.Equal("Navigation", d.Category));

        Assert.True(catalog.TryGet("nav.destinationName", out var name));
        Assert.Equal("Tim Hortons", name.Read());

        Assert.True(catalog.TryGet("nav.destinationAddress", out var address));
        Assert.Equal("Portage Avenue, Winnipeg", address.Read());

        Assert.True(catalog.TryGet("nav.destinationKind", out var kind));
        Assert.Equal("Fast food", kind.Read());

        Assert.True(catalog.TryGet("nav.destinationHours", out var hours));
        Assert.Equal("Mo-Su 06:00-22:00", hours.Read());

        Assert.True(catalog.TryGet("nav.destinationPhone", out var phone));
        Assert.Equal("+1 204 555 0100", phone.Read());

        Assert.True(catalog.TryGet("nav.destinationWebsite", out var site));
        Assert.Equal("https://example.test", site.Read());

        // The route's own wording, not a second opinion about it — and no decimal above ten
        // kilometres, which is RouteSummary's rule rather than this one's.
        Assert.True(catalog.TryGet("nav.routeSummary", out var summary));
        Assert.Equal("12 km · 18 min", summary.Read());
    }

    /// <summary>
    /// A closure rather than a stored value, so a destination chosen after the card was built is
    /// on it anyway — which is the ordinary case, since the cards outlive every search.
    /// </summary>
    [Fact]
    public void A_readout_reads_the_destination_as_it_is_now()
    {
        GeocodeResult? chosen = null;
        var catalog = ReadoutCatalog.ForNavigation(() => chosen, () => null);

        Assert.True(catalog.TryGet("nav.destinationName", out var name));
        Assert.Null(name.Read());

        chosen = Shop;

        Assert.Equal("Tim Hortons", name.Read());
    }

    [Fact]
    public void With_nothing_chosen_every_readout_is_empty() =>
        Assert.All(Catalog().Definitions, d => Assert.True(string.IsNullOrEmpty(d.Read())));

    [Fact]
    public void An_unknown_readout_id_is_not_in_the_catalog() =>
        Assert.False(Catalog(Shop).TryGet("nav.whatTheDriverIsThinking", out _));

    // ---- The picker ----

    [Fact]
    public void Readouts_are_offered_with_no_unit_no_range_and_no_rate()
    {
        var choice = ValueChoice.From(Catalog(Shop)).Single(c => c.Id == "nav.destinationName");

        Assert.Equal(CardSource.Readout, choice.Source);
        Assert.True(choice.IsWords);
        Assert.Equal(string.Empty, choice.Unit);
        Assert.False(choice.HasRange);
        Assert.Equal(0, choice.DefaultRateHz);
        Assert.Equal("Navigation", choice.Category);
        Assert.Contains("words", choice.Detail, StringComparison.Ordinal);

        // No unit means no unit choice: a name cannot be shown in Fahrenheit.
        Assert.Single(choice.UnitChoices);
    }

    [Fact]
    public void Readouts_come_after_the_signals_and_the_sensors()
    {
        var choices = ValueChoice.All(
            SignalCatalog.FromJson("[]"),
            SensorCatalog.Empty,
            Catalog(Shop));

        Assert.Equal(7, choices.Count);
        Assert.All(choices, c => Assert.Equal(CardSource.Readout, c.Source));
    }

    /// <summary>A dash built without a map offers none, and the old two-catalog call still works.</summary>
    [Fact]
    public void Without_readouts_nothing_changes() =>
        Assert.Empty(ValueChoice.All(SignalCatalog.FromJson("[]"), SensorCatalog.Empty));

    // ---- The value ----

    [Fact]
    public void A_readout_value_is_words_and_not_a_number()
    {
        Assert.True(Catalog(Shop).TryGet("nav.destinationName", out var definition));
        using var value = new ReadoutCardValue(definition);

        Assert.Equal("Tim Hortons", value.Words);
        Assert.True(double.IsNaN(value.Value));
        Assert.Equal(string.Empty, value.Unit);
        Assert.Equal(SignalQuality.Live, value.Quality);
        Assert.True(value.IsUsable);
        Assert.Equal("FROM THE MAP", value.Footer);
    }

    [Fact]
    public void A_readout_with_nothing_behind_it_is_unavailable()
    {
        Assert.True(Catalog().TryGet("nav.destinationName", out var definition));
        using var value = new ReadoutCardValue(definition);

        Assert.Null(value.Words);
        Assert.False(value.IsUsable);
        Assert.Equal(SignalQuality.Unavailable, value.Quality);
        Assert.Equal("nothing chosen", value.Footer);
    }

    /// <summary>
    /// OSM leaves fields blank as often as it leaves them out, and a card showing three spaces
    /// looks like a bug rather than like an absence.
    /// </summary>
    [Fact]
    public void Blank_words_count_as_no_words()
    {
        var catalog = new ReadoutCatalog([new("test.blank", "Blank", "Navigation", () => "   ")]);

        Assert.True(catalog.TryGet("test.blank", out var definition));
        using var value = new ReadoutCardValue(definition);

        Assert.Null(value.Words);
        Assert.False(value.IsUsable);
    }

    [Fact]
    public void Words_are_trimmed()
    {
        var catalog = new ReadoutCatalog([new("test.padded", "Padded", "Navigation", () => "  Tim Hortons  ")]);

        Assert.True(catalog.TryGet("test.padded", out var definition));
        using var value = new ReadoutCardValue(definition);

        Assert.Equal("Tim Hortons", value.Words);
    }

    // ---- On a card ----

    [Fact]
    public void A_card_on_a_readout_draws_the_words()
    {
        var card = Card("nav.destinationName", Catalog(Shop));

        card.Activate();

        Assert.True(card.IsWords);
        Assert.Equal("Tim Hortons", card.Text);
        Assert.Equal(string.Empty, card.UnitText);
        Assert.Equal("FROM THE MAP", card.FooterText);
        Assert.Equal(SignalQuality.Live, card.Quality);
    }

    /// <summary>
    /// The guard this feature exists to get past. Every other card treats NaN as nothing to draw,
    /// and a readout has no number at all — so without the one check in <c>Text</c>, a destination
    /// would render as a dash.
    /// </summary>
    [Fact]
    public void A_readout_is_not_mistaken_for_a_missing_number()
    {
        var card = Card("nav.destinationName", Catalog(Shop));

        card.Activate();

        Assert.NotEqual(Abstractions.Wpf.ObservableSignal.Placeholder, card.Text);
    }

    [Fact]
    public void A_readout_with_nothing_behind_it_draws_the_placeholder()
    {
        var card = Card("nav.destinationName", Catalog());

        card.Activate();

        Assert.Equal(Abstractions.Wpf.ObservableSignal.Placeholder, card.Text);
        Assert.True(card.IsWords);
    }

    /// <summary>Words have no range, so a card asking for a bar does not get one.</summary>
    [Fact]
    public void A_readout_is_never_drawn_as_a_bar()
    {
        var card = Card("nav.destinationName", Catalog(Shop), style: CardStyle.Bar);

        card.Activate();

        Assert.False(card.IsBar);
        Assert.Equal(0, card.BarFraction);
    }

    /// <summary>
    /// A card switched from a temperature to a readout must not keep the unit: °C beside a shop's
    /// name is the kind of confidently wrong thing this project refuses.
    /// </summary>
    [Fact]
    public void A_readout_does_not_inherit_a_stored_unit()
    {
        var catalog = Catalog(Shop);
        var choice = ValueChoice.From(catalog).Single(c => c.Id == "nav.destinationName");
        var spec = new CardSpec
        {
            SignalId = "nav.destinationName",
            Source = nameof(CardSource.Readout),
            Unit = nameof(DisplayUnit.Fahrenheit),
        };

        var card = new WidgetCardViewModel(Factory(catalog), spec, choice);
        card.Activate();

        Assert.Equal(string.Empty, card.UnitText);
    }

    [Fact]
    public void A_card_naming_a_readout_that_does_not_exist_has_no_value()
    {
        var catalog = Catalog(Shop);
        var choice = ValueChoice.From(catalog).First();
        var spec = new CardSpec { SignalId = "nav.nonsense", Source = nameof(CardSource.Readout) };

        var card = new WidgetCardViewModel(Factory(catalog), spec, choice);
        card.Activate();

        Assert.False(card.IsActive);
        Assert.Equal("suspended", card.FooterText);
    }

    [Fact]
    public void A_readout_card_stores_its_source_so_it_reloads_as_one()
    {
        var spec = new CardSpec { SignalId = "nav.destinationName", Source = "Readout" };

        Assert.Equal(CardSource.Readout, spec.ParsedSource);
    }

    private static WidgetCardViewModel Card(string id, ReadoutCatalog catalog, CardStyle style = CardStyle.Number)
    {
        var choice = ValueChoice.From(catalog).Single(c => c.Id == id);
        var spec = new CardSpec
        {
            SignalId = id,
            Source = nameof(CardSource.Readout),
            Style = style.ToString(),
        };

        return new WidgetCardViewModel(Factory(catalog), spec, choice);
    }

    /// <summary>
    /// A factory whose signal and sensor halves are stubs: a readout card touches neither, and
    /// that it touches neither is part of what these pin.
    /// </summary>
    private static CardValueFactory Factory(ReadoutCatalog catalog) =>
        new(
            new StubBus(),
            new SensorService(
                SensorCatalog.Empty,
                new StubBus(),
                SystemClock.Instance,
                new StubDevice(),
                new StubMount()),
            catalog);

    private sealed class StubBus : IVehicleSignals
    {
        public IReadOnlyCollection<string> KnownSignals => [];

        public SignalValue Current(string signalId) => SignalValue.Missing(signalId);

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) =>
            throw new NotSupportedException();

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz) =>
            throw new ArgumentException($"Signal '{signalId}' is not in the catalog.", nameof(signalId));
    }

    private sealed class StubMount : IMountReferenceStore
    {
        public MountReference Load() => new();

        public void Save(MountReference reference)
        {
        }
    }

    private sealed class StubDevice : IDeviceSensors
    {
        public bool Has(SensorDefinition definition) => false;

        public SensorReading Read(SensorDefinition definition, MountReference reference) =>
            SensorReading.None("NO SENSOR");

        public MountReference? CaptureReference(DateTimeOffset nowUtc) => null;

        public void Dispose()
        {
        }
    }
}
