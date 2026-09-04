using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// An auxiliary instrument cluster in the 2019 F-150's style, and the stage's idle default.
/// </summary>
/// <remarks>
/// It shows what the factory cluster leaves out — boost, oil temperature, voltage, intake air,
/// throttle and load — drawn the way the cluster would draw them (see
/// <see cref="GaugesViewModel"/> and <see cref="Gauges.RoundGauge"/>). It is the default the
/// stage falls back to rather than the clock (F12): a truck's home screen wanting to show
/// gauges is a better idle than a clock, and it means there is no arbitrary "last occupant" to
/// restore on ignition.
/// <para>
/// Plain WPF, like the clock — no hosted child window, so it composes with the shell and the
/// airspace rules that shaped the launcher do not apply.
/// </para>
/// </remarks>
public sealed class GaugesStageOccupant(IVehicleSignals signals) : IStageOccupant
{
    private readonly GaugesViewModel _viewModel = new(signals);

    /// <inheritdoc />
    public string Name => "GAUGES";

    /// <inheritdoc />
    public FrameworkElement CreateView() => new GaugesView { DataContext = _viewModel };

    /// <inheritdoc />
    public string Describe() => $"gauges {_viewModel.Summary}";

    /// <inheritdoc />
    public void Dispose() => _viewModel.Dispose();
}
