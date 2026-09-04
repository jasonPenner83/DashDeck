using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The gauges stage: an auxiliary instrument cluster, in the 2019 F-150's style, showing the
/// things the factory cluster does not.
/// </summary>
/// <remarks>
/// <b>Deliberately none of the cluster's own gauges.</b> Speed, tach, fuel and coolant are on
/// the physical cluster already; repeating them here would be a second copy of something the
/// driver can already see. So this reads the values the cluster leaves out — turbo boost,
/// oil temperature, battery voltage, intake air, throttle and load — and draws them the way
/// the cluster would have.
/// <para>
/// Every value is a named signal declared through the arbiter while the stage is up (like the
/// compass, ADR-0016): the gauges cost request budget only as long as they are on screen.
/// Boost is the one derived reading — manifold pressure above barometric, in psi — which is
/// exactly the sort of thing a component or a gauge computes rather than the bus supplying.
/// </para>
/// </remarks>
public sealed partial class GaugesViewModel : ObservableObject, IDisposable
{
    private const double KPaToPsi = 0.1450377;

    private readonly ObservableSignal _manifold;
    private readonly ObservableSignal _baro;
    private readonly ObservableSignal _oil;
    private readonly ObservableSignal _volts;
    private readonly ObservableSignal _iat;
    private readonly ObservableSignal _throttle;
    private readonly ObservableSignal _load;
    private readonly ObservableSignal[] _all;

    public GaugesViewModel(IVehicleSignals signals)
    {
        _manifold = new ObservableSignal(signals, "engine.intakeManifoldPressure", SignalPriority.Normal, 3);
        _baro = new ObservableSignal(signals, "engine.barometricPressure", SignalPriority.Low, 0.5);
        _oil = new ObservableSignal(signals, "engine.oilTemp", SignalPriority.Normal, 1);
        _volts = new ObservableSignal(signals, "vehicle.controlModuleVoltage", SignalPriority.Normal, 1);
        _iat = new ObservableSignal(signals, "engine.intakeAirTemp", SignalPriority.Normal, 0.5);
        _throttle = new ObservableSignal(signals, "engine.throttlePosition", SignalPriority.Normal, 4);
        _load = new ObservableSignal(signals, "engine.load", SignalPriority.Normal, 2);
        _all = [_manifold, _baro, _oil, _volts, _iat, _throttle, _load];

        foreach (var signal in _all)
        {
            signal.PropertyChanged += (_, _) => Recompute();
        }

        Recompute();
    }

    /// <summary>Turbo boost, psi above atmospheric — negative is vacuum.</summary>
    public double Boost { get; private set; }

    public double OilTemp { get; private set; }

    public double Volts { get; private set; }

    public double Iat { get; private set; }

    public double Throttle { get; private set; }

    public double Load { get; private set; }

    private void Recompute()
    {
        // Boost is manifold minus barometric; below atmospheric it is vacuum. Barometric barely
        // moves, so a missing reading falls back to standard sea-level pressure rather than
        // zeroing the whole gauge — only the manifold reading, which does swing, has to be live.
        var baro = _baro.IsUsable ? _baro.Value : 101.325;
        Boost = _manifold.IsUsable ? (_manifold.Value - baro) * KPaToPsi : 0;

        OilTemp = _oil.IsUsable ? _oil.Value : 0;
        Volts = _volts.IsUsable ? _volts.Value : 0;
        Iat = _iat.IsUsable ? _iat.Value : 0;
        Throttle = _throttle.IsUsable ? _throttle.Value : 0;
        Load = _load.IsUsable ? _load.Value : 0;

        OnPropertyChanged(nameof(Boost));
        OnPropertyChanged(nameof(OilTemp));
        OnPropertyChanged(nameof(Volts));
        OnPropertyChanged(nameof(Iat));
        OnPropertyChanged(nameof(Throttle));
        OnPropertyChanged(nameof(Load));
    }

    /// <summary>A one-line summary for the occupant's Describe().</summary>
    public string Summary =>
        $"boost={Boost:0.0}psi oil={OilTemp:0}°C volts={Volts:0.0} iat={Iat:0}°C throttle={Throttle:0}% load={Load:0}%";

    public void Dispose()
    {
        foreach (var signal in _all)
        {
            signal.Dispose();
        }
    }
}
