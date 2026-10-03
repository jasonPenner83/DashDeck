using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.Sensors;
using DashDeck.Host.Stage.Gauges;

namespace DashDeck.Host.Stage;

/// <summary>
/// The stage drawn from a layout file (ADR-0037), and the stage's idle default.
/// </summary>
/// <remarks>
/// Out of the box it is the auxiliary instrument cluster in the F-150's style — boost, oil
/// temperature, voltage, intake air, throttle and load, what the factory cluster leaves out. That
/// is now the built-in <see cref="StageLayout"/>, and any layout — the theme's, or one written by
/// hand — replaces it: gauges, text, the clock and panels, anywhere on the stage. It is the default
/// the stage falls back to rather than the clock (F12).
/// </remarks>
/// <param name="name">What the launcher calls it (ADR-0038) — GAUGES, COMPASS, or TOWING for a pinned layout.</param>
/// <param name="sensors">The tablet's sensors, truck first, for compass, G meter and sensor gauges (ADR-0039).</param>
public sealed class GaugesStageOccupant(
    IVehicleSignals signals,
    IClock clock,
    StageLayoutService layouts,
    string name = "GAUGES",
    SensorService? sensors = null) : IStageOccupant
{
    private StageLayoutView? _view;

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public FrameworkElement CreateView() => _view = new StageLayoutView(layouts, signals, clock, sensors);

    /// <summary>
    /// RELOAD picks up a layout edited by hand, without leaving the stage; RESET PEAK G is there
    /// whenever the layout has a G meter, as it was on the old compass screen.
    /// </summary>
    public IReadOnlyList<StageAction> Actions => layouts.Current.HasGMeter
        ? [new StageAction("RESET PEAK G", () => _view?.ResetPeaks()), new StageAction("RELOAD STAGE LAYOUT", layouts.Reload)]
        : [new StageAction("RELOAD STAGE LAYOUT", layouts.Reload)];

    /// <inheritdoc />
    public string Describe() => _view?.Describe() ?? $"layout={layouts.Current.Id}";

    /// <inheritdoc />
    public void Dispose() => _view?.Dispose();
}
