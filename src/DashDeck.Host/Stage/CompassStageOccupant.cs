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
/// Three bands. The rose, the G meter and the attitude readouts sit side by side across 850,
/// and leaving three bands below gives the dash three rows of cards rather than two — the
/// arrangement the row arithmetic was rewritten for (ADR-0015).
/// </para>
/// </remarks>
public sealed class CompassStageOccupant(IVehicleSignals signals, SensorService sensors)
    : IStageOccupant
{
    private readonly CompassViewModel _viewModel = new(signals, sensors);

    /// <inheritdoc />
    public string Name => "COMPASS";

    /// <inheritdoc />
    public int PreferredBands => 3;

    /// <inheritdoc />
    public FrameworkElement CreateView() => new CompassView { DataContext = _viewModel };

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
