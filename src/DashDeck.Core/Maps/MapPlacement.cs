namespace DashDeck.Core.Maps;

/// <summary>
/// Where the map is drawn. It is the one destination with more than one size.
/// </summary>
/// <remarks>
/// Every other destination owns exactly one region: the cards, the console and the climate
/// panel draw below the stage, and Settings takes the lot. A map is the first thing that
/// genuinely wants a choice — glanced at beside the gauges while you know where you are, and
/// filling the screen when you do not — so the size is state rather than a layout constant.
/// <para>
/// Three placements, not two, because the stage is the third place a map can live (ADR-0058
/// has it arriving as a WebView2 occupant). The handoff is modelled from the start so it is a
/// case in this enum rather than a retrofit through the shell's geometry later.
/// </para>
/// </remarks>
public enum MapPlacement
{
    /// <summary>The bottom third, below the stage — where the cards and the climate panel draw.</summary>
    Panel,

    /// <summary>Everything between the status strip and the nav: all six bands, stage collapsed.</summary>
    Full,

    /// <summary>Handed to the stage, as an occupant, with whatever the nav chooses below it.</summary>
    Stage,
}
