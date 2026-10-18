namespace DashDeck.Core.Maps;

/// <summary>
/// The colours a map is drawn in, as a theme resolved them (ADR-0064).
/// </summary>
/// <remarks>
/// The seam between the theme layer and the map. Themes live in the shell and resolve to WPF
/// brushes; this is the handful of values the map needs, as plain <c>#RRGGBB</c> text, so that
/// <see cref="MapStyle"/> stays pure and testable without WPF, a browser or a signal.
/// <para>
/// Every one of these is a theme token with a default that follows an existing one, so a theme
/// that says nothing about maps still gets a map that matches it — and night derivation comes
/// free, because the tokens go through the same dimming rules as everything else.
/// </para>
/// </remarks>
/// <param name="Land">Everything that is not something else.</param>
/// <param name="Water">Lakes, rivers and the sea.</param>
/// <param name="Green">Woods, grass and parks.</param>
/// <param name="Building">Building footprints, close in.</param>
/// <param name="Road">Minor roads and streets.</param>
/// <param name="RoadMajor">Motorways, trunk roads and primaries.</param>
/// <param name="RoadCasing">The outline under a road, which is what separates it from the land.</param>
/// <param name="Label">Place and street names.</param>
/// <param name="LabelHalo">What is drawn behind a label so it reads over anything.</param>
/// <param name="Marker">The vehicle.</param>
public readonly record struct MapPalette(
    string Land,
    string Water,
    string Green,
    string Building,
    string Road,
    string RoadMajor,
    string RoadCasing,
    string Label,
    string LabelHalo,
    string Marker)
{
    /// <summary>The DashDeck look, for tests and for a map asked for before a theme is resolved.</summary>
    public static MapPalette Default { get; } = new(
        Land: "#0D0C0B",
        Water: "#0F1A24",
        Green: "#141712",
        Building: "#211F1C",
        Road: "#6B665E",
        RoadMajor: "#A5A096",
        RoadCasing: "#0D0C0B",
        Label: "#F4F1EB",
        LabelHalo: "#0D0C0B",
        Marker: "#FF7A1A");

    /// <summary>Every colour in it, for validation.</summary>
    public IEnumerable<string> Colours =>
        [Land, Water, Green, Building, Road, RoadMajor, RoadCasing, Label, LabelHalo, Marker];
}
