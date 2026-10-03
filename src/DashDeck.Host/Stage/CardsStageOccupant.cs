using System.Windows;
using DashDeck.Host.ViewModels;
using DashDeck.Host.Views;

namespace DashDeck.Host.Stage;

/// <summary>
/// Your cards on the stage (ADR-0041): the arranged dash (ADR-0015) that used to sit in the two
/// bands below it, before the console took them.
/// </summary>
/// <remarks>
/// One <see cref="DashboardViewModel"/> for the life of the shell — the cards, their order and
/// the page you were on survive the occupant coming and going. While this occupant is on the
/// stage the dashboard is shown, so its visible page asks the truck for data; when something
/// replaces it, nothing on any page does. Editing, adding and the full-screen card editor work
/// as they always did: MODIFY WIDGETS in the three-dot menu, and the bar that replaces the nav.
/// </remarks>
public sealed class CardsStageOccupant : IStageOccupant
{
    private readonly DashboardViewModel _dashboard;

    public CardsStageOccupant(DashboardViewModel dashboard, string name = "CARDS")
    {
        _dashboard = dashboard;
        Name = name;
    }

    public string Name { get; }

    public FrameworkElement CreateView()
    {
        _dashboard.IsShown = true;
        return new DashboardView { DataContext = _dashboard };
    }

    public IReadOnlyList<StageAction> Actions =>
        [new StageAction(_dashboard.IsEditing ? "DONE EDITING CARDS" : "EDIT CARDS", () => _dashboard.ToggleEditCommand.Execute(null))];

    public string Describe() =>
        $"cards page {_dashboard.PageIndex + 1}/{_dashboard.Pages.Count}, shown={_dashboard.IsShown}";

    public void Dispose()
    {
        // Leaving the stage: stop editing (its bar replaces the nav, which would strand you) and
        // withdraw every card's declaration.
        if (_dashboard.IsEditing)
        {
            _dashboard.ToggleEditCommand.Execute(null);
        }

        _dashboard.IsShown = false;
    }
}
