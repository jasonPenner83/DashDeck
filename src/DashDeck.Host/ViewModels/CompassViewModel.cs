using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;
using DashDeck.Host.Sensors;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The compass stage: where the truck is pointing, how it is sitting, and what it is doing.
/// </summary>
/// <remarks>
/// Three clusters and nothing else — a heading, an attitude, and a G meter. Everything on it
/// answers "what is the vehicle doing right now", which is what keeps it from becoming a
/// panel of numbers: anything that does not answer that belongs on a dash card, and the dash
/// can hold as many as you like now.
/// <para>
/// Every value comes through <see cref="SensorService"/>, so each is truck-first with a
/// labelled tablet fallback (ADR-0016). None of them is supplied by the truck yet.
/// </para>
/// </remarks>
public sealed partial class CompassViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How often everything is re-read.
    /// </summary>
    /// <remarks>
    /// Costs nothing on the request budget: reading a truck value is a dictionary lookup
    /// against the state bus and reading a tablet one is a sensor poll. Neither is vehicle
    /// traffic — the rate the arbiter cares about was declared once, by the sensor service.
    /// <para>
    /// Faster than the compass alone needed, because a G meter that updates eight times a
    /// second reads as broken. The accelerometer's own minimum interval is 10 ms.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(60);

    private readonly SensorService _sensors;
    private readonly DispatcherTimer _timer;

    private double _smoothedHeading = double.NaN;
    private double _smoothedLateral;
    private double _smoothedLongitudinal;

    [ObservableProperty]
    private string _headingText = "———";

    [ObservableProperty]
    private string _cardinalText = "——";

    [ObservableProperty]
    private string _headingSource = "———";

    [ObservableProperty]
    private SignalQuality _headingQuality = SignalQuality.Unavailable;

    /// <summary>Rose rotation. Negative, because the card turns under a fixed marker.</summary>
    [ObservableProperty]
    private double _roseAngle;

    [ObservableProperty]
    private string _pitchText = "——";

    [ObservableProperty]
    private string _rollText = "——";

    [ObservableProperty]
    private SignalQuality _attitudeQuality = SignalQuality.Unavailable;

    /// <summary>Lateral g, positive to the right. Drives the ball's horizontal position.</summary>
    [ObservableProperty]
    private double _lateralG;

    /// <summary>Longitudinal g, positive forward. Braking is negative.</summary>
    [ObservableProperty]
    private double _longitudinalG;

    [ObservableProperty]
    private string _gText = "—.——";

    [ObservableProperty]
    private string _peakText = "—.——";

    [ObservableProperty]
    private SignalQuality _motionQuality = SignalQuality.Unavailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelCaption))]
    private bool _isLevelled;

    public CompassViewModel(IVehicleSignals signals, SensorService sensors)
    {
        _sensors = sensors;
        IsLevelled = sensors.IsLevelled;

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

    /// <summary>The largest total g seen since levelling. Reset by re-levelling.</summary>
    public double PeakG { get; private set; }

    /// <summary>What the level control says.</summary>
    public string LevelCaption => IsLevelled ? "RE-LEVEL" : "LEVEL";

    /// <summary>True when the attitude and G readings are waiting on a levelled mount.</summary>
    public bool NeedsLevelling => !IsLevelled;

    /// <summary>
    /// Capture the tablet's current orientation as level and forward.
    /// </summary>
    /// <remarks>
    /// Done once, parked, on flat ground, with the tablet in its mount. Everything relative to
    /// the mount refuses to render a number until it has been.
    /// </remarks>
    [RelayCommand]
    private void Level()
    {
        if (_sensors.Level())
        {
            IsLevelled = _sensors.IsLevelled;

            // A peak carried over from the old reference is measured against axes that no
            // longer exist, so it is not a number about this mount any more.
            PeakG = 0;
            OnPropertyChanged(nameof(NeedsLevelling));
        }
    }

    /// <summary>Forget the largest g seen so far and start again.</summary>
    [RelayCommand]
    private void ResetPeak()
    {
        PeakG = 0;
        PeakText = "0.00";
    }

    /// <summary>One line describing the state, for <c>--shot</c>.</summary>
    public string Describe() =>
        $"heading={HeadingText} cardinal={CardinalText} from={HeadingSource} " +
        $"pitch={PitchText} roll={RollText} g={GText} peak={PeakText} levelled={IsLevelled}";

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        Speed.Dispose();
        Outside.Dispose();
    }

    /// <summary>
    /// The compass point, to eight of them.
    /// </summary>
    /// <remarks>
    /// Sixteen would be more precise and less readable at a glance, and the number is right
    /// beside it for anyone who wants precision. The half-sector bias is what makes each point
    /// own the 45° <em>centred</em> on it rather than the 45° starting at it — without it, due
    /// north reads as north-east.
    /// </remarks>
    public static string Cardinal(double degrees)
    {
        if (double.IsNaN(degrees))
        {
            return "——";
        }

        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

        var normalised = ((degrees % 360) + 360) % 360;
        return points[(int)Math.Floor(((normalised + 22.5) % 360) / 45)];
    }

    private void Refresh()
    {
        // Read rather than remembered. Caching it at construction meant the flag could drift
        // from the service that actually owns it — levelling by any route other than this
        // view model's own command left the readings live and the screen still saying they
        // were not. Duplicated state, and the copy on screen was the wrong one.
        if (IsLevelled != _sensors.IsLevelled)
        {
            IsLevelled = _sensors.IsLevelled;
            OnPropertyChanged(nameof(NeedsLevelling));
        }

        RefreshHeading();
        RefreshAttitude();
        RefreshMotion();
    }

    private void RefreshHeading()
    {
        var reading = _sensors.Read("attitude.heading");

        HeadingSource = reading.Source;
        HeadingQuality = reading.Quality;

        if (!reading.IsUsable)
        {
            HeadingText = "———";
            CardinalText = "——";
            _smoothedHeading = double.NaN;
            return;
        }

        _smoothedHeading = SmoothAngle(_smoothedHeading, reading.Value);

        HeadingText = string.Create(CultureInfo.CurrentCulture, $"{_smoothedHeading:000}");
        CardinalText = Cardinal(_smoothedHeading);
        RoseAngle = -_smoothedHeading;
    }

    private void RefreshAttitude()
    {
        var pitch = _sensors.Read("attitude.pitch");
        var roll = _sensors.Read("attitude.roll");

        AttitudeQuality = pitch.IsUsable ? pitch.Quality : roll.Quality;

        PitchText = Degrees(pitch);
        RollText = Degrees(roll);
    }

    private void RefreshMotion()
    {
        var lateral = _sensors.Read("motion.lateralG");
        var longitudinal = _sensors.Read("motion.longitudinalG");

        MotionQuality = lateral.IsUsable ? lateral.Quality : longitudinal.Quality;

        if (!lateral.IsUsable || !longitudinal.IsUsable)
        {
            LateralG = 0;
            LongitudinalG = 0;
            GText = "—.——";
            return;
        }

        // Lightly smoothed. An accelerometer in a vehicle picks up the road surface as well as
        // the driving, and a ball that vibrates is unreadable at exactly the moment it matters.
        _smoothedLateral = Smooth(_smoothedLateral, lateral.Value);
        _smoothedLongitudinal = Smooth(_smoothedLongitudinal, longitudinal.Value);

        LateralG = _smoothedLateral;
        LongitudinalG = _smoothedLongitudinal;

        var total = Math.Sqrt((_smoothedLateral * _smoothedLateral)
            + (_smoothedLongitudinal * _smoothedLongitudinal));

        GText = string.Create(CultureInfo.CurrentCulture, $"{total:0.00}");

        if (total > PeakG)
        {
            PeakG = total;
            PeakText = string.Create(CultureInfo.CurrentCulture, $"{PeakG:0.00}");
        }
    }

    /// <summary>
    /// One decimal, and never <c>-0.0</c>.
    /// </summary>
    /// <remarks>
    /// A roll of -0.04 rounds to a minus sign in front of a zero, which reads as a fault
    /// rather than as level. Nudging the value off exact zero before formatting is the
    /// smallest fix that does not lie about the sign of anything that actually has one.
    /// </remarks>
    private static string Degrees(SensorReading reading)
    {
        if (!reading.IsUsable)
        {
            return "——";
        }

        var value = Math.Abs(reading.Value) < 0.05 ? 0 : reading.Value;
        return string.Create(CultureInfo.CurrentCulture, $"{value:0.0}");
    }

    private static double Smooth(double previous, double next, double factor = 0.35) =>
        (previous * (1 - factor)) + (next * factor);

    /// <summary>
    /// Ease the needle towards a new bearing.
    /// </summary>
    /// <remarks>
    /// Averaged as a <em>vector</em>, not as a number. Averaging degrees directly puts the
    /// mean of 350° and 10° at 180° — a compass that swings to due south every time it crosses
    /// north. Converting to a unit vector and back is the fix, and it is the only arithmetic
    /// on this screen that is not obvious.
    /// </remarks>
    private static double SmoothAngle(double previous, double next, double factor = 0.25)
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
