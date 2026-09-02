using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;
using DashDeck.Host.Stage;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The compass stage: a bearing, where it came from, and two things worth knowing beside it.
/// </summary>
/// <remarks>
/// Deliberately three numbers and a rose. A compass that also carried altitude, a trip meter
/// and a G-meter would be a worse compass — the point of the stage is the one big glanceable
/// thing, and the dash below it is where a dozen numbers belong now that it can hold them.
/// <para>
/// Speed and outside temperature come from the truck, as named signals through the same
/// <see cref="ObservableSignal"/> every card uses. The heading tries to.
/// </para>
/// </remarks>
public sealed partial class CompassViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How often the heading is re-read.
    /// </summary>
    /// <remarks>
    /// Costs nothing on the request budget either way: reading the truck's heading is a
    /// dictionary lookup against the state bus, and reading the tablet's is a sensor poll.
    /// Neither is vehicle traffic — the rate the arbiter cares about was declared once, by
    /// <see cref="TruckHeadingSource"/>.
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(120);

    private readonly IHeadingSource _heading;
    private readonly DispatcherTimer _timer;

    private double _smoothed = double.NaN;

    [ObservableProperty]
    private string _headingText = "———";

    [ObservableProperty]
    private string _cardinalText = "——";

    [ObservableProperty]
    private string _sourceText = "NO HEADING SOURCE";

    [ObservableProperty]
    private SignalQuality _quality = SignalQuality.Unavailable;

    /// <summary>Rose rotation. Negative, because the card turns under a fixed marker.</summary>
    [ObservableProperty]
    private double _roseAngle;

    public CompassViewModel(IVehicleSignals signals, IHeadingSource? heading = null)
    {
        // Truck first, tablet second — see PreferredHeadingSource. Injectable so the
        // preference can be tested without a magnetometer or a truck.
        _heading = heading ?? new PreferredHeadingSource(
            new TruckHeadingSource(signals),
            new DeviceHeadingSource());

        Speed = new ObservableSignal(signals, "vehicle.speed", SignalPriority.Normal, 1, "0");
        Outside = new ObservableSignal(signals, "ambient.airTemp", SignalPriority.Low, 0.1, "0");

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tick };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    /// <summary>Road speed, from the truck.</summary>
    public ObservableSignal Speed { get; }

    /// <summary>Outside air temperature, from the truck.</summary>
    public ObservableSignal Outside { get; }

    /// <summary>True while there is a bearing worth drawing.</summary>
    public bool HasHeading => Quality is SignalQuality.Live or SignalQuality.Simulated;

    /// <summary>One line describing the state, for <c>--shot</c>.</summary>
    public string Describe() =>
        $"heading={HeadingText} cardinal={CardinalText} source={SourceText} quality={Quality}";

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _heading.Dispose();
        Speed.Dispose();
        Outside.Dispose();
    }

    private void Refresh()
    {
        var reading = _heading.Read();

        SourceText = reading.Source;
        Quality = reading.Quality;
        OnPropertyChanged(nameof(HasHeading));

        if (!reading.IsUsable)
        {
            HeadingText = "———";
            CardinalText = "——";
            _smoothed = double.NaN;
            return;
        }

        _smoothed = Smooth(_smoothed, reading.Degrees);

        HeadingText = string.Create(CultureInfo.CurrentCulture, $"{_smoothed:000}");
        CardinalText = reading.Cardinal;
        RoseAngle = -_smoothed;
    }

    /// <summary>
    /// Ease the needle towards a new bearing.
    /// </summary>
    /// <remarks>
    /// Averaged as a <em>vector</em>, not as a number. Averaging degrees directly puts the
    /// mean of 350° and 10° at 180° — a compass that swings to due south every time it
    /// crosses north. Converting to a unit vector and back is the fix, and it is the only
    /// arithmetic on this screen that is not obvious.
    /// <para>
    /// Needed because a magnetometer jitters by a degree or two at rest, and a rose that
    /// vibrates reads as broken even when the bearing is right.
    /// </para>
    /// </remarks>
    private static double Smooth(double previous, double next, double factor = 0.25)
    {
        if (double.IsNaN(previous))
        {
            return next;
        }

        var p = previous * Math.PI / 180;
        var n = next * Math.PI / 180;

        var x = (Math.Cos(p) * (1 - factor)) + (Math.Cos(n) * factor);
        var y = (Math.Sin(p) * (1 - factor)) + (Math.Sin(n) * factor);

        return ((Math.Atan2(y, x) * 180 / Math.PI) + 360) % 360;
    }
}
