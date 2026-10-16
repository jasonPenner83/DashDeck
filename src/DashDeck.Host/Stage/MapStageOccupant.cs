using System.Windows;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// The map as a stage occupant — the third of its three placements.
/// </summary>
/// <remarks>
/// Plain WPF, like the clock face, so the airspace rules that shaped the launcher do not apply
/// to it and the overflow menu draws over it correctly. <b>That will change when the real map
/// arrives:</b> ADR-0058 puts MapLibre in WebView2, which is a child window, and then this
/// occupant inherits every rule a hosted window has — see Q43 for the part that is genuinely
/// unresolved, which is that a WebView2 cannot be moved between the stage and the nav region
/// without reloading it.
/// <para>
/// It shares the one <see cref="MapViewModel"/> with the nav region rather than making a second:
/// the map is in exactly one placement at a time, so at most one of the two views is alive, and
/// the position readout and the placement stay the same object either way.
/// </para>
/// </remarks>
/// <param name="map">The shared map view-model, which also holds the placement.</param>
/// <param name="bringBack">
/// What the menu's one action does: take the map off the stage and back to the size it had.
/// Deliberately not called from <see cref="Dispose"/> — being replaced on the stage by something
/// else is reconciled by the shell after the switch, because doing it here would run in the
/// middle of the switch itself.
/// </param>
public sealed class MapStageOccupant(MapViewModel map, Action bringBack) : IStageOccupant
{
    /// <inheritdoc />
    public string Name => "MAPS";

    /// <inheritdoc />
    public FrameworkElement CreateView() => new Views.MapPanel { DataContext = map };

    /// <inheritdoc />
    /// <remarks>
    /// Two, and they are the two the panel offers that still mean something here. An occupant's
    /// controls belong in the menu rather than drawn inside its own picture — doubly so for this
    /// one, which is a child window the menu has to be told to stand down for (ADR-0021).
    /// </remarks>
    public IReadOnlyList<StageAction> Actions =>
    [
        new StageAction("BRING THE MAP BACK", bringBack),

        // The panel's chips are hidden on the stage, so this is the only way back to the vehicle
        // after a pan — and a map that has lost you with no way to recentre is worse than none.
        new StageAction("RECENTRE", () => map.RecentreCommand.Execute(null)),
    ];

    /// <inheritdoc />
    public string Describe() => $"map on the stage · {map.Describe()}";

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to release. The view-model is the shell's and outlives every placement — that is
    /// the point of there being one of it — and the view is a plain <c>UserControl</c>. When
    /// ADR-0058's map arrives this is where its WebView2 goes, and this comment stops being true.
    /// </remarks>
    public void Dispose()
    {
    }
}
