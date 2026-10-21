namespace DashDeck.Core.Maps;

/// <summary>What is drawn under the roads (ADR-0071).</summary>
public enum MapBase
{
    /// <summary>The theme's own cartography (ADR-0064). The default.</summary>
    Road,

    /// <summary>Imagery from above, with the theme's roads and names drawn over it.</summary>
    Satellite,

    /// <summary>The road map, with the shape of the ground shaded into it.</summary>
    Terrain,
}

/// <summary>Which way is up on the map (ADR-0071).</summary>
public enum MapOrientation
{
    /// <summary>North at the top, whatever the truck is doing. The default.</summary>
    NorthUp,

    /// <summary>The way the truck is travelling at the top, while the map follows it.</summary>
    TrackUp,
}

/// <summary>A button drawn over the map, pressed (ADR-0071).</summary>
public enum MapControl
{
    /// <summary>Open the address search.</summary>
    Search,

    /// <summary>Swap north up and track up.</summary>
    Orientation,

    /// <summary>Show or hide the range lines.</summary>
    Range,

    /// <summary>Put the map back on the truck.</summary>
    Recentre,

    /// <summary>The road map.</summary>
    Road,

    /// <summary>Imagery.</summary>
    Satellite,

    /// <summary>The road map with the ground shaded.</summary>
    Terrain,
}

/// <summary>
/// The map's own settings, the ones its overlay buttons change (ADR-0071).
/// </summary>
/// <remarks>
/// A value, kept outside the page for the same reason the camera is (Q43): the map is rebuilt on
/// every placement change, and a map that forgot it was on satellite every time it was resized
/// would be a map that argued with you. Saved, too, so it opens the way it was left.
/// </remarks>
/// <param name="Base">What is drawn under the roads.</param>
/// <param name="Orientation">Which way is up.</param>
/// <param name="ShowRange">Whether the range lines are drawn.</param>
public readonly record struct MapChoices(MapBase Base, MapOrientation Orientation, bool ShowRange)
{
    /// <summary>The road map, north up, with the range lines on.</summary>
    public static MapChoices Default { get; } = new(MapBase.Road, MapOrientation.NorthUp, ShowRange: true);

    /// <summary>The same choices with the other orientation.</summary>
    public MapChoices Turned() => this with
    {
        Orientation = Orientation == MapOrientation.NorthUp ? MapOrientation.TrackUp : MapOrientation.NorthUp,
    };

    /// <summary>The same choices with the range lines the other way.</summary>
    public MapChoices RangeToggled() => this with { ShowRange = !ShowRange };

    /// <summary>The same choices, with what a pressed button changes changed.</summary>
    /// <remarks>
    /// Search and Recentre are not choices — they do something rather than change how the map is
    /// drawn — so they leave this alone, and the caller acts on them.
    /// </remarks>
    public MapChoices With(MapControl control) => control switch
    {
        MapControl.Orientation => Turned(),
        MapControl.Range => RangeToggled(),
        MapControl.Road => this with { Base = MapBase.Road },
        MapControl.Satellite => this with { Base = MapBase.Satellite },
        MapControl.Terrain => this with { Base = MapBase.Terrain },
        _ => this,
    };

    /// <summary>Read the saved form. Anything unreadable falls back to the default for that part.</summary>
    public static MapChoices From(string? savedBase, string? savedOrientation, bool showRange) => new(
        Enum.TryParse<MapBase>(savedBase, ignoreCase: true, out var chosenBase) && Enum.IsDefined(chosenBase)
            ? chosenBase
            : MapBase.Road,
        Enum.TryParse<MapOrientation>(savedOrientation, ignoreCase: true, out var chosenOrientation)
            && Enum.IsDefined(chosenOrientation)
            ? chosenOrientation
            : MapOrientation.NorthUp,
        showRange);

    /// <summary>For <c>--shot</c>: <c>road · north up · range on</c>.</summary>
    public string Describe() =>
        $"{Base.ToString().ToLowerInvariant()} · {(Orientation == MapOrientation.TrackUp ? "track up" : "north up")}"
        + $" · range {(ShowRange ? "on" : "off")}";
}
