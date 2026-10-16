using System.Windows.Controls;

namespace DashDeck.Host.Views;

/// <summary>
/// Address search over the map (ADR-0065). Bound to <see cref="ViewModels.MapViewModel"/>, whose
/// <c>Search</c> holds the query, the keyboard and the results.
/// </summary>
/// <remarks>
/// Bound to the map's view-model rather than the search's own, so one DataContext reaches both the
/// search and the commands that close it and move the map.
/// </remarks>
public partial class MapSearchPanel : UserControl
{
    public MapSearchPanel() => InitializeComponent();
}
