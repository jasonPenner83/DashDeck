using System.Windows.Controls;

namespace DashDeck.Host.Views;

/// <summary>
/// The MAPS destination's picture. Bound to <see cref="ViewModels.MapViewModel"/>.
/// </summary>
/// <remarks>
/// One view for all three placements — bottom third, full screen, and on the stage — because the
/// placement changes the region it is drawn in rather than what is drawn. The shell holds the
/// region; <see cref="Stage.MapStageOccupant"/> holds the stage one.
/// </remarks>
public partial class MapPanel : UserControl
{
    public MapPanel() => InitializeComponent();
}
