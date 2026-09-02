using System.Windows.Controls;

namespace DashDeck.Host.Stage;

/// <summary>The clock and weather face. Bound to <see cref="ViewModels.ClockWeatherViewModel"/>.</summary>
public partial class ClockWeatherView : UserControl
{
    public ClockWeatherView() => InitializeComponent();
}
