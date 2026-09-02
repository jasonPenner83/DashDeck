using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// A compass, and the two numbers that belong beside one.
/// </summary>
/// <remarks>
/// The first occupant to ask the truck for anything. The clock face fetches its own weather
/// and the web applets know nothing about the vehicle at all, so until now the stage was a
/// place things were displayed rather than a consumer of vehicle data. This one takes
/// <see cref="IVehicleSignals"/> like any component would, and declares through the same
/// arbiter — three signals, modestly: heading if the truck can supply it, speed at 1 Hz and
/// ambient temperature at 0.1 Hz.
/// <para>
/// Three bands rather than four. A rose does not need 780 pixels, and leaving three bands
/// below gives the dash three rows of cards instead of two — which is the arrangement the
/// row arithmetic was rewritten for (ADR-0015).
/// </para>
/// </remarks>
public sealed class CompassStageOccupant(IVehicleSignals signals, IHeadingSource? heading = null)
    : IStageOccupant
{
    private readonly CompassViewModel _viewModel = new(signals, heading);

    /// <inheritdoc />
    public string Name => "COMPASS";

    /// <inheritdoc />
    public int PreferredBands => 3;

    /// <inheritdoc />
    public FrameworkElement CreateView() => new CompassView { DataContext = _viewModel };

    /// <inheritdoc />
    public string Describe() => _viewModel.Describe();

    /// <inheritdoc />
    public void Dispose() => _viewModel.Dispose();
}
