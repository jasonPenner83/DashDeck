namespace DashDeck.Core.Maps;

/// <summary>
/// Which of its three placements the map is in, and which size it returns to.
/// </summary>
/// <remarks>
/// A value rather than a pair of booleans on the shell, for one reason: the stage handoff has
/// to be reversible, and "bring it back" has to land somewhere. Somewhere is the size it had
/// before it went — a map sent to the stage from full screen that came back as a bottom-third
/// strip would have quietly lost the thing you set.
/// <para>
/// Pure, and in Core, so the whole rule set is tested without a UI thread. The shell turns a
/// placement into bands and regions (<c>ShellViewModel</c>); nothing about WPF belongs here.
/// </para>
/// </remarks>
public readonly record struct MapView
{
    /// <summary>Where the map opens: the bottom third, beside whatever is on the stage.</summary>
    /// <remarks>
    /// The small size is the default deliberately. A destination that took the whole screen the
    /// first time it was tapped would cover the gauges to show a placeholder — and when the real
    /// map lands, the common case is still a glance rather than a study.
    /// </remarks>
    public static readonly MapView Default = new(MapPlacement.Panel, MapPlacement.Panel);

    private MapView(MapPlacement placement, MapPlacement restores)
    {
        Placement = placement;
        Restores = restores;
    }

    /// <summary>Where the map is now.</summary>
    public MapPlacement Placement { get; }

    /// <summary>
    /// The size it goes back to when it comes off the stage. Never <see cref="MapPlacement.Stage"/>.
    /// </summary>
    public MapPlacement Restores { get; }

    /// <summary>True while the map draws below the stage, in the bottom third.</summary>
    public bool IsPanel => Placement is MapPlacement.Panel;

    /// <summary>True while the map covers all six bands and the stage is collapsed behind it.</summary>
    public bool IsFull => Placement is MapPlacement.Full;

    /// <summary>True while the map is a stage occupant.</summary>
    public bool IsOnStage => Placement is MapPlacement.Stage;

    /// <summary>True while the map draws in the region below the stage — either size.</summary>
    public bool IsInTheNav => !IsOnStage;

    /// <summary>
    /// Swap the two in-nav sizes. Does nothing while the map is on the stage, where the
    /// occupant has the whole picture and there is no second size to offer.
    /// </summary>
    public MapView Resized() => Placement switch
    {
        MapPlacement.Panel => new MapView(MapPlacement.Full, MapPlacement.Full),
        MapPlacement.Full => new MapView(MapPlacement.Panel, MapPlacement.Panel),
        _ => this,
    };

    /// <summary>
    /// Hand the map to the stage, remembering the size to come back to. Idempotent.
    /// </summary>
    public MapView SentToTheStage() => IsOnStage ? this : new MapView(MapPlacement.Stage, Placement);

    /// <summary>
    /// Take the map off the stage, back to the size it had before it went. Idempotent, which
    /// matters: the occupant reports its own disposal, and being replaced on the stage by
    /// something else arrives here as well as tapping BRING IT BACK.
    /// </summary>
    public MapView BroughtBack() => IsOnStage ? new MapView(Restores, Restores) : this;

    /// <summary>What the size button would do next, in the words on the button.</summary>
    /// <remarks>
    /// The caption names the destination size, not the current one: a control labelled with the
    /// state you are already in is the oldest toggle mistake there is.
    /// </remarks>
    public string ResizeCaption => Placement switch
    {
        MapPlacement.Panel => "FULL SCREEN",
        MapPlacement.Full => "BOTTOM THIRD",
        _ => "ON THE STAGE",
    };

    /// <summary>Where the map is, for <c>--shot</c> and the startup trail.</summary>
    public string Describe() => Placement switch
    {
        MapPlacement.Panel => "bottom third",
        MapPlacement.Full => "full screen",
        _ => $"on the stage (returns to {Describe(Restores)})",
    };

    private static string Describe(MapPlacement placement) =>
        placement is MapPlacement.Full ? "full screen" : "bottom third";
}
