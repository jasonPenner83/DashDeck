using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace DashDeck.Host.Views;

/// <summary>
/// Address search over the map (ADR-0065). Bound to <see cref="ViewModels.MapViewModel"/>, whose
/// <c>Search</c> holds the query and the results.
/// </summary>
/// <remarks>
/// Bound to the map's view-model rather than the search's own, so one DataContext reaches both the
/// search and the commands that close it and move the map.
/// <para>
/// The only thing the code-behind does is get the field ready to type into: focus it and put the
/// caret at the end. Selecting it is what brings the touch keyboard up, when no keyboard is
/// attached (<see cref="Shell.TouchKeyboard"/>, ADR-0079).
/// </para>
/// </remarks>
public partial class MapSearchPanel : UserControl
{
    public MapSearchPanel()
    {
        InitializeComponent();

        // IsVisibleChanged rather than Loaded: the panel stays in the tree and is shown and hidden
        // by the region around it, so Loaded fires once and never again.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Ready();
            }
        };
    }

    private void Ready()
    {
        // After layout, or the focus lands on a field that is not arranged yet and is ignored.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (!Field.IsEnabled)
                {
                    // Moving: the gate has the keyboard shut, and asking Windows for one would be
                    // the dash undoing its own safety rule.
                    return;
                }

                Field.Focus();
                Keyboard.Focus(Field);
                Field.CaretIndex = Field.Text.Length;
            });
    }
}
