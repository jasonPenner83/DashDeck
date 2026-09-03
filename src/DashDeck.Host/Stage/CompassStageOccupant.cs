using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.Sensors;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// Where the truck is pointing, how it is sitting, and what it is doing.
/// </summary>
/// <remarks>
/// The first occupant to ask the truck for anything. The clock face fetches its own weather
/// and the web applets know nothing about the vehicle, so until this the stage was a place
/// things were displayed rather than a consumer of vehicle data.
/// <para>
/// Levelling lives on the action bar rather than beside the readouts. It is done once, parked,
/// and a control you use once does not belong in the middle of the numbers you read constantly.
/// </para>
/// </remarks>
public sealed class CompassStageOccupant(IVehicleSignals signals, SensorService sensors)
    : IStageOccupant
{
    private readonly CompassViewModel _viewModel = new(signals, sensors);

    /// <inheritdoc />
    public string Name => "COMPASS";

    /// <inheritdoc />
    public FrameworkElement CreateView() => new CompassView { DataContext = _viewModel };

    /// <summary>
    /// Only the peak reset. Levelling moved to Settings.
    /// </summary>
    /// <remarks>
    /// It is a calibration done once, parked, on flat ground — not something reached for while
    /// driving — so it belongs with the other things you set and leave rather than on the one
    /// screen you look at most.
    /// </remarks>
    public IReadOnlyList<StageAction> Actions =>
        [new StageAction("RESET PEAK G", () => _viewModel.ResetPeakCommand.Execute(null))];

    /// <inheritdoc />
    public string Describe() => _viewModel.Describe();

    /// <summary>
    /// Disposes the view model, not the sensor service.
    /// </summary>
    /// <remarks>
    /// The service outlives the occupant deliberately: it holds the mount reference and any
    /// vehicle declarations, and re-levelling every time the stage changed would be absurd.
    /// </remarks>
    public void Dispose() => _viewModel.Dispose();
}
