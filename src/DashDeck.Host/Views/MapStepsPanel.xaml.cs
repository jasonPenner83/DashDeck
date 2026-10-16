using System.Windows.Controls;

namespace DashDeck.Host.Views;

/// <summary>
/// The turn list for a route (ADR-0065's addendum). Bound to <see cref="ViewModels.MapViewModel"/>.
/// </summary>
public partial class MapStepsPanel : UserControl
{
    public MapStepsPanel() => InitializeComponent();
}
