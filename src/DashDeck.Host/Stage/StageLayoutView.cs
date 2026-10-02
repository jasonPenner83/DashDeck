using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;
using DashDeck.Host.Sensors;
using DashDeck.Host.Stage.Gauges;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Stage;

/// <summary>
/// Draws a <see cref="StageLayout"/> (ADR-0037): every element at its position on the layout's
/// canvas — the stage's 912 × 636, or the climate panel's 912 × 390 (ADR-0040) — scaled to
/// whatever the region really is.
/// </summary>
/// <remarks>
/// Owns the signal declarations for the gauges it draws, so a gauge costs request budget only while
/// it is on screen (ADR-0004), and redraws from scratch when the layout changes or is reloaded —
/// which is also what drops the old declarations.
/// <para>
/// Sensor readings — the compass, the G meter, and any gauge whose source is a sensor (ADR-0039) —
/// are polled on a fast timer that runs only while the layout has one. Reading a sensor is a
/// lookup, not vehicle traffic: the sensor service declared the truck signals it prefers once.
/// </para>
/// </remarks>
public sealed class StageLayoutView : UserControl, IDisposable
{
    private readonly StageLayoutService _layouts;
    private readonly IVehicleSignals _signals;
    private readonly IClock _clock;
    private readonly Canvas _canvas = new() { Width = StageLayout.StageWidth, Height = StageLayout.StageHeight, ClipToBounds = true };
    private readonly List<IDisposable> _owned = [];
    private readonly List<(GaugeSpec Spec, Func<GaugeReading> Read)> _gauges = [];
    private readonly DispatcherTimer _tick;
    private readonly List<(TextBlock Text, string Format)> _clocks = [];
    private readonly SensorService? _sensors;
    private readonly DispatcherTimer _sensorTick;
    private readonly List<Action> _sensorReaders = [];
    private readonly List<CompassFace> _compasses = [];
    private readonly List<GMeterState> _gMeters = [];

    /// <summary>
    /// How often sensors are re-read. Faster than a heading needs, because a G meter that updates
    /// eight times a second reads as broken.
    /// </summary>
    private static readonly TimeSpan SensorInterval = TimeSpan.FromMilliseconds(60);

    public StageLayoutView(StageLayoutService layouts, IVehicleSignals signals, IClock clock, SensorService? sensors = null)
    {
        _layouts = layouts;
        _signals = signals;
        _clock = clock;
        _sensors = sensors;

        _sensorTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = SensorInterval };
        _sensorTick.Tick += (_, _) => ReadSensors();

        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => TickClocks();
        _tick.Start();

        _layouts.LayoutChanged += OnLayoutChanged;
        Build(_layouts.Current);
    }

    /// <summary>A line for the occupant's Describe(): the layout, and each gauge's reading.</summary>
    public string Describe() =>
        $"layout={_layouts.Current.Id} " + string.Join(' ', _gauges.Select(g =>
        {
            var r = g.Read();
            return $"{(g.Spec.Id.Length > 0 ? g.Spec.Id : g.Spec.Label)}={(r.HasValue ? r.Value.ToString("0.#", CultureInfo.InvariantCulture) : "-")}/{r.Quality}";
        }).Concat(_compasses.Select(c => $"heading={(double.IsNaN(c.Heading) ? "-" : c.Heading.ToString("0", CultureInfo.InvariantCulture))}/{SensorMath.Cardinal(c.Heading)}"))
          .Concat(_gMeters.Select(m => $"g={(double.IsNaN(m.Lateral) ? "-" : SensorMath.Total(m.Lateral, m.Longitudinal).ToString("0.00", CultureInfo.InvariantCulture))} peak={m.Peak.ToString("0.00", CultureInfo.InvariantCulture)}")));

    /// <summary>Forget every G meter's peak and start again — RESET PEAK G in the three-dot menu.</summary>
    public void ResetPeaks()
    {
        foreach (var meter in _gMeters)
        {
            meter.Peak = 0;
        }

        ReadSensors();
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => Dispatcher.Invoke(() => Build(_layouts.Current));

    private void Build(StageLayout layout)
    {
        Release();
        _canvas.Children.Clear();
        _canvas.Width = layout.Canvas.Width;
        _canvas.Height = layout.Canvas.Height;

        Paint(_canvas, Panel.BackgroundProperty, layout.Background, "@canvas");

        foreach (var element in layout.Elements)
        {
            var visual = element.Type switch
            {
                StageElementType.Gauge or StageElementType.Setpoint or StageElementType.Levels or StageElementType.Indicator
                    when element.Source.IsSensor => SensorGauge(element, FaceFor(element)),
                StageElementType.Gauge or StageElementType.Setpoint or StageElementType.Levels or StageElementType.Indicator
                    => Gauge(element, FaceFor(element)),
                StageElementType.Glass => new GlassFace(element),
                StageElementType.Text => Text(element, element.Content),
                StageElementType.Clock => Clock(element),
                StageElementType.Compass => Compass(element),
                StageElementType.GMeter => GMeter(element),
                _ => PanelShape(element),
            };

            Canvas.SetLeft(visual, element.X);
            Canvas.SetTop(visual, element.Y);
            _canvas.Children.Add(visual);
        }

        TickClocks();

        if (_sensorReaders.Count > 0)
        {
            ReadSensors();
            _sensorTick.Start();
        }
    }

    /// <summary>The face that draws a reading element: a gauge, or a climate setpoint, levels or indicator.</summary>
    private static IReadingFace FaceFor(GaugeSpec spec) => spec.Type switch
    {
        StageElementType.Setpoint => new SetpointFace(spec),
        StageElementType.Levels => new LevelsFace(spec),
        StageElementType.Indicator => new IndicatorFace(spec),
        _ => new GaugeFace(spec),
    };

    private void ReadSensors()
    {
        foreach (var read in _sensorReaders)
        {
            read();
        }
    }

    /// <summary>A sensor read through the service, or "no sensors" when the view has none — never a guess.</summary>
    private SensorReading ReadSensor(string id) =>
        _sensors?.Read(id) ?? SensorReading.None("NO SENSORS");

    /// <summary>A gauge whose source is a sensor (ADR-0039): truck first, tablet second, and it says which.</summary>
    private FrameworkElement SensorGauge(GaugeSpec spec, IReadingFace face)
    {
        var id = spec.Source.Sensor!.Trim();

        if (_sensors is not null && !_sensors.Catalog.TryGet(id, out _))
        {
            face.MarkUnknown("UNKNOWN SENSOR");
            face.ShowSource(id);
            _gauges.Add((spec, () => new GaugeReading(double.NaN, SignalQuality.Unavailable)));
            return (FrameworkElement)face;
        }

        var last = new GaugeReading(double.NaN, SignalQuality.Unavailable);
        _gauges.Add((spec, () => last));
        _sensorReaders.Add(() =>
        {
            var reading = ReadSensor(id);
            last = GaugeReading.FromSensor(spec.Source, reading.Value, reading.Quality);
            face.Show(last);
            face.ShowSource(reading.Source);
        });

        return (FrameworkElement)face;
    }

    /// <summary>A compass rose (ADR-0039), eased toward each new heading as a vector so it never swings through south.</summary>
    private FrameworkElement Compass(GaugeSpec spec)
    {
        var face = new CompassFace(spec);
        var id = spec.Source.Sensor?.Trim() ?? "attitude.heading";
        var smoothed = double.NaN;

        _compasses.Add(face);
        _sensorReaders.Add(() =>
        {
            var reading = ReadSensor(id);
            if (!reading.IsUsable)
            {
                smoothed = double.NaN;
                face.Show(double.NaN, reading.Quality, reading.Source);
                return;
            }

            smoothed = SensorMath.SmoothAngle(smoothed, reading.Value);
            face.Show(smoothed, reading.Quality, reading.Source);
        });

        return face;
    }

    /// <summary>A G meter (ADR-0039): lateral and longitudinal g, lightly smoothed, with a peak.</summary>
    private FrameworkElement GMeter(GaugeSpec spec)
    {
        var face = new GMeterFace(spec);
        var state = new GMeterState();
        var reference = _sensors?.Reference.CapturedUtc;

        _gMeters.Add(state);
        _sensorReaders.Add(() =>
        {
            // A peak measured against an old mount reference is about axes that no longer exist.
            if (_sensors?.Reference.CapturedUtc != reference)
            {
                reference = _sensors?.Reference.CapturedUtc;
                state.Peak = 0;
            }

            var lateral = ReadSensor("motion.lateralG");
            var longitudinal = ReadSensor("motion.longitudinalG");
            var levelled = _sensors?.IsLevelled ?? false;
            var quality = lateral.IsUsable ? lateral.Quality : longitudinal.Quality;

            if (!lateral.IsUsable || !longitudinal.IsUsable)
            {
                state.Lateral = double.NaN;
                state.Longitudinal = double.NaN;
                face.Show(double.NaN, double.NaN, state.Peak, quality, lateral.Source, levelled);
                return;
            }

            state.Lateral = SensorMath.Smooth(double.IsNaN(state.Lateral) ? 0 : state.Lateral, lateral.Value);
            state.Longitudinal = SensorMath.Smooth(double.IsNaN(state.Longitudinal) ? 0 : state.Longitudinal, longitudinal.Value);
            state.Peak = Math.Max(state.Peak, SensorMath.Total(state.Lateral, state.Longitudinal));

            face.Show(state.Lateral, state.Longitudinal, state.Peak, quality, lateral.Source, levelled);
        });

        return face;
    }

    /// <summary>One G meter's smoothed reading and its peak.</summary>
    private sealed class GMeterState
    {
        public double Lateral { get; set; } = double.NaN;

        public double Longitudinal { get; set; } = double.NaN;

        public double Peak { get; set; }
    }

    private FrameworkElement Gauge(GaugeSpec spec, IReadingFace face)
    {
        var primary = Observe(spec.Source.Signal, spec.Source.RateHz);
        var minus = spec.Source.Minus is { } m ? Observe(m, Math.Max(0.2, spec.Source.RateHz / 4)) : null;

        if (primary is null || (spec.Source.Minus is not null && minus is null && spec.Source.MinusFallback is null))
        {
            // Not in the catalog. The gauge says so rather than drawing a scale for nothing.
            face.MarkUnknown("UNKNOWN SIGNAL");
            _gauges.Add((spec, () => new GaugeReading(double.NaN, SignalQuality.Unavailable)));
            return (FrameworkElement)face;
        }

        GaugeReading Read() => GaugeReading.Compute(spec.Source, primary.Current, minus?.Current);

        void Update(object? sender, PropertyChangedEventArgs e) => face.Show(Read());

        primary.PropertyChanged += Update;
        if (minus is not null)
        {
            minus.PropertyChanged += Update;
        }

        _gauges.Add((spec, Read));
        face.Show(Read());
        return (FrameworkElement)face;
    }

    /// <summary>Declare one signal for as long as this layout is up, or null for one the catalog lacks.</summary>
    private ObservableSignal? Observe(string signalId, double rateHz)
    {
        try
        {
            var signal = new ObservableSignal(_signals, signalId, SignalPriority.Normal, rateHz);
            _owned.Add(signal);
            return signal;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static TextBlock Text(GaugeSpec spec, string content)
    {
        var text = new TextBlock
        {
            Text = content,
            Width = spec.Width,
            Height = spec.Height,
            FontSize = spec.FontSize,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = spec.Align.ToLowerInvariant() switch
            {
                "center" or "centre" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };

        text.SetResourceReference(TextBlock.FontFamilyProperty, spec.Font.Equals("ui", StringComparison.OrdinalIgnoreCase) ? "UiFont" : "MonoFont");
        Paint(text, TextBlock.ForegroundProperty, spec.Colour, "@textHigh");
        return text;
    }

    private TextBlock Clock(GaugeSpec spec)
    {
        var text = Text(spec, "");
        _clocks.Add((text, spec.Content.Length > 0 ? spec.Content : "HH:mm"));
        return text;
    }

    private static Border PanelShape(GaugeSpec spec)
    {
        var r = StageLayout.ParseRadius(spec.Radius) ?? [0, 0, 0, 0];
        var panel = new Border
        {
            Width = spec.Width,
            Height = spec.Height,
            CornerRadius = new CornerRadius(r[0], r[1], r[2], r[3]),
        };

        Paint(panel, Border.BackgroundProperty, spec.Colour, "@surface");
        return panel;
    }

    private void TickClocks()
    {
        // IClock, never DateTime.Now — a replayed drive shows the time it happened.
        var now = _clock.UtcNow.ToLocalTime();
        foreach (var (text, format) in _clocks)
        {
            text.Text = now.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    /// <summary>A layout colour: <c>@token</c> as a live theme reference, <c>#RRGGBB</c> as itself.</summary>
    private static void Paint(FrameworkElement target, DependencyProperty property, string? colour, string fallback)
    {
        foreach (var candidate in new[] { colour, fallback })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.StartsWith('@') && ThemeTokens.TryGet(candidate[1..], out var token) && token.Kind is TokenKind.Colour)
            {
                target.SetResourceReference(property, token.ResourceKey);
                return;
            }

            if (ThemeColour.TryParse(candidate, out var c))
            {
                var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
                brush.Freeze();
                target.SetValue(property, brush);
                return;
            }
        }
    }

    /// <summary>Drop every signal declaration this layout made.</summary>
    private void Release()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _owned.Clear();
        _gauges.Clear();
        _clocks.Clear();
        _sensorTick.Stop();
        _sensorReaders.Clear();
        _compasses.Clear();
        _gMeters.Clear();
    }

    public void Dispose()
    {
        _tick.Stop();
        _layouts.LayoutChanged -= OnLayoutChanged;
        Release();
    }
}
