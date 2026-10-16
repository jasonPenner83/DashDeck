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
    /// One action, and it is the way out. The panel's own buttons are hidden here, because a
    /// control drawn inside an occupant costs picture and competes with the menu that already
    /// exists for exactly this.
    /// </remarks>
    public IReadOnlyList<StageAction> Actions => [new StageAction("BRING THE MAP BACK", bringBack)];

    /// <inheritdoc />
    public string Describe() => $"map on the stage · {map.PositionText}";

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
