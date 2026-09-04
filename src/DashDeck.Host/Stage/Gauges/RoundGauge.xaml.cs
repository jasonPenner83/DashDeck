using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// A round analog gauge, drawn in the 2019 F-150's cluster idiom.
/// </summary>
/// <remarks>
/// <b>Custom-drawn, not data-bound</b> — the second rendering idiom ADR-0001/ADR-0011 always
/// anticipated and F5 was waiting for. A needle sweeping a dial is not something a
/// <c>TextBlock</c> binding expresses; it is a face built once and an angle animated. So the
/// static parts — bezel, ticks, numerals, redline — are laid down once in <see cref="Build"/>,
/// and only the needle moves, eased toward each new value the way a real meter's does rather
/// than snapping.
/// <para>
/// The gauges it draws are deliberately the ones the factory cluster does <em>not</em> show —
/// boost, oil temperature, voltage — in the cluster's own style. One control serves the big
/// dials and the small ones: sweep, tick spacing, and whether the ends read as numbers or as
/// letters are all properties.
/// </para>
/// </remarks>
public partial class RoundGauge : UserControl
{
    // The 2019 F-150 cluster is ice-blue on matte black: white markings, a cyan-white needle
    // that glows, a small dark pivot, and an orange-red redline. The old palette — a white
    // needle on a fat orange hub — was the tell that this was not a Ford. See the reference the
    // owner shot of the real cluster.
    private static readonly Color NeedleColor = (Color)ColorConverter.ConvertFromString("#B9F1F7");
    private static readonly Color GlowColor = (Color)ColorConverter.ConvertFromString("#2FD4E6");
    private static readonly Brush Face = Frozen("#08090A");
    private static readonly Brush Bezel = Frozen("#1E2023");
    private static readonly Brush InnerRing = Frozen("#34383C");
    private static readonly Brush TickMajor = Frozen("#EDEFF2");
    private static readonly Brush TickMinor = Frozen("#7C848C");
    private static readonly Brush Numerals = Frozen("#EDEFF2");
    private static readonly Brush Redline = Frozen("#E8531E");
    private static readonly Brush NeedleBrush = new SolidColorBrush(NeedleColor);
    private static readonly Brush HubFace = Frozen("#16181A");
    private static readonly Brush HubRing = Frozen("#4A4E52");
    private static readonly Brush HubDot = new SolidColorBrush(GlowColor);
    private static readonly Brush LabelBrush = Frozen("#AEB6BE");

    private RotateTransform? _needleRotate;
    private TextBlock? _digital;

    public RoundGauge()
    {
        InitializeComponent();

        // Property sets during XAML parse rebuild before the control is in the tree, where
        // theme fonts do not resolve; a rebuild on load settles it with the real resources.
        Loaded += (_, _) => Build();
    }

    private FontFamily Font(string key) =>
        TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RoundGauge), new PropertyMetadata(0.0, OnRebuild));

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RoundGauge), new PropertyMetadata(100.0, OnRebuild));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RoundGauge), new PropertyMetadata(0.0, OnValueChanged));

    public double MajorTick { get => (double)GetValue(MajorTickProperty); set => SetValue(MajorTickProperty, value); }
    public static readonly DependencyProperty MajorTickProperty =
        DependencyProperty.Register(nameof(MajorTick), typeof(double), typeof(RoundGauge), new PropertyMetadata(10.0, OnRebuild));

    public double MinorTick { get => (double)GetValue(MinorTickProperty); set => SetValue(MinorTickProperty, value); }
    public static readonly DependencyProperty MinorTickProperty =
        DependencyProperty.Register(nameof(MinorTick), typeof(double), typeof(RoundGauge), new PropertyMetadata(5.0, OnRebuild));

    /// <summary>Numerals show <c>value / NumeralDivisor</c> — 1000 for a tach reading 0–7.</summary>
    public double NumeralDivisor { get => (double)GetValue(NumeralDivisorProperty); set => SetValue(NumeralDivisorProperty, value); }
    public static readonly DependencyProperty NumeralDivisorProperty =
        DependencyProperty.Register(nameof(NumeralDivisor), typeof(double), typeof(RoundGauge), new PropertyMetadata(1.0, OnRebuild));

    /// <summary>Where the redline starts, or NaN for none.</summary>
    public double RedlineFrom { get => (double)GetValue(RedlineFromProperty); set => SetValue(RedlineFromProperty, value); }
    public static readonly DependencyProperty RedlineFromProperty =
        DependencyProperty.Register(nameof(RedlineFrom), typeof(double), typeof(RoundGauge), new PropertyMetadata(double.NaN, OnRebuild));

    /// <summary>Needle at minimum, degrees from straight up, clockwise positive.</summary>
    public double StartAngle { get => (double)GetValue(StartAngleProperty); set => SetValue(StartAngleProperty, value); }
    public static readonly DependencyProperty StartAngleProperty =
        DependencyProperty.Register(nameof(StartAngle), typeof(double), typeof(RoundGauge), new PropertyMetadata(-135.0, OnRebuild));

    /// <summary>Total sweep in degrees.</summary>
    public double SweepAngle { get => (double)GetValue(SweepAngleProperty); set => SetValue(SweepAngleProperty, value); }
    public static readonly DependencyProperty SweepAngleProperty =
        DependencyProperty.Register(nameof(SweepAngle), typeof(double), typeof(RoundGauge), new PropertyMetadata(270.0, OnRebuild));

    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    public static readonly DependencyProperty DiameterProperty =
        DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(RoundGauge), new PropertyMetadata(320.0, OnRebuild));

    /// <summary>The metric's name, over the hub — "BOOST", "OIL".</summary>
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(RoundGauge), new PropertyMetadata(string.Empty, OnRebuild));

    /// <summary>The unit, under the hub — "psi", "°C", "V".</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(RoundGauge), new PropertyMetadata(string.Empty, OnRebuild));

    /// <summary>When set, the ends read as letters (E/C) not numbers.</summary>
    public string LowLabel { get => (string)GetValue(LowLabelProperty); set => SetValue(LowLabelProperty, value); }
    public static readonly DependencyProperty LowLabelProperty =
        DependencyProperty.Register(nameof(LowLabel), typeof(string), typeof(RoundGauge), new PropertyMetadata(string.Empty, OnRebuild));

    public string HighLabel { get => (string)GetValue(HighLabelProperty); set => SetValue(HighLabelProperty, value); }
    public static readonly DependencyProperty HighLabelProperty =
        DependencyProperty.Register(nameof(HighLabel), typeof(string), typeof(RoundGauge), new PropertyMetadata(string.Empty, OnRebuild));

    /// <summary>Numeric format for the digital readout under the hub, e.g. <c>0.0</c>.</summary>
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public static readonly DependencyProperty ValueFormatProperty =
        DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(RoundGauge), new PropertyMetadata("0", OnRebuild));

    /// <summary>True to show the live digital value under the hub. Off for the small gauges.</summary>
    public bool ShowDigital { get => (bool)GetValue(ShowDigitalProperty); set => SetValue(ShowDigitalProperty, value); }
    public static readonly DependencyProperty ShowDigitalProperty =
        DependencyProperty.Register(nameof(ShowDigital), typeof(bool), typeof(RoundGauge), new PropertyMetadata(true, OnRebuild));

    private bool Lettered => !string.IsNullOrEmpty(LowLabel);

    private bool Big => Diameter > 200;

    private static void OnRebuild(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((RoundGauge)d).Build();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((RoundGauge)d).MoveNeedle();

    private void Build()
    {
        if (Diameter <= 0)
        {
            return;
        }

        var d = Diameter;
        var c = d / 2;
        Host.Children.Clear();
        Width = d;
        Height = d;

        // Bezel and matte-black face.
        Host.Children.Add(Disc(1, 1, d - 2, null, Bezel, 5));
        Host.Children.Add(Disc(8, 8, d - 16, Face, InnerRing, 1.5));

        var rTick = c - 22;
        var rMajorIn = rTick - (Big ? 20 : 12);
        var rMinorIn = rTick - (Big ? 11 : 7);
        var rNum = rMajorIn - (Big ? 24 : 15);

        if (!double.IsNaN(RedlineFrom) && RedlineFrom < Maximum)
        {
            Host.Children.Add(Arc(c, rTick - 3, AngleFor(RedlineFrom), AngleFor(Maximum), Redline, Big ? 6 : 4));
        }

        for (var v = Minimum; v <= Maximum + 1e-6; v += MinorTick)
        {
            var major = IsMultiple(v, MajorTick);
            var a = AngleFor(v);
            var outer = OnCircle(c, c, rTick, a);
            var inner = OnCircle(c, c, major ? rMajorIn : rMinorIn, a);

            Host.Children.Add(new Line
            {
                X1 = outer.X, Y1 = outer.Y, X2 = inner.X, Y2 = inner.Y,
                Stroke = major ? TickMajor : TickMinor,
                StrokeThickness = major ? (Big ? 3 : 2) : 1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });

            // Only the big dials carry a numeric scale. The F-150's minor gauges are ticks and a
            // readout, no numbers — and at 164px the numbers only crowded the caption anyway.
            if (major && !Lettered && Big)
            {
                Text((v / NumeralDivisor).ToString("0", CultureInfo.InvariantCulture),
                    OnCircle(c, c, rNum, a), 26, FontWeights.SemiBold, Numerals, "UiFont");
            }
        }

        if (Lettered)
        {
            Text(LowLabel, OnCircle(c, c, rNum, AngleFor(Minimum)), 18, FontWeights.SemiBold, LabelBrush, "UiFont");
            Text(HighLabel, OnCircle(c, c, rNum, AngleFor(Maximum)), 18, FontWeights.SemiBold, LabelBrush, "UiFont");
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            Text(Caption, new Point(c, c + (Big ? 42 : 20)), Big ? 15 : 10, FontWeights.SemiBold, LabelBrush, "MonoFont");
        }

        // The live digital value, under the caption. Its own TextBlock, updated on each reading
        // rather than by rebuilding the whole face.
        if (ShowDigital)
        {
            _digital = new TextBlock
            {
                Foreground = Numerals,
                FontFamily = Font("UiFont"),
                FontSize = Big ? 30 : 17,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Width = d,   // full width and centred, so a widening number stays put
                Text = DigitalText(),
            };
            Canvas.SetLeft(_digital, 0);
            Canvas.SetTop(_digital, c + (Big ? 62 : 42));
            Host.Children.Add(_digital);
        }
        else
        {
            _digital = null;
        }

        // Needle: a thin cyan-white spear pointing up with a short counterweight, pivoted at
        // centre and lit with a cyan glow — the F-150's needle reads as a lit filament, not a
        // painted blade. It rides above a small dark hub, the opposite of the old fat orange cap.
        var tip = c - 22 - (Big ? 8 : 4);
        var baseHalf = Big ? 4.0 : 2.6;
        var tail = Big ? 20.0 : 11.0;
        var needle = new Polygon
        {
            Fill = NeedleBrush,
            Points =
            [
                new Point(c, c - tip),
                new Point(c + baseHalf, c),
                new Point(c, c + tail),
                new Point(c - baseHalf, c),
            ],
            RenderTransform = _needleRotate = new RotateTransform(AngleFor(Value), c, c),
            Effect = new DropShadowEffect
            {
                Color = GlowColor,
                BlurRadius = Big ? 16 : 9,
                ShadowDepth = 0,
                Opacity = 0.9,
            },
        };
        Host.Children.Add(needle);

        // Hub: a small dark disc with a metallic ring and a lit cyan centre, so the needle looks
        // pinned to a real spindle rather than growing out of a blob.
        var rHub = Big ? 13.0 : 8.0;
        Host.Children.Add(Disc(c - rHub, c - rHub, rHub * 2, HubFace, HubRing, Big ? 2 : 1.5));
        var rDot = Big ? 4.0 : 2.6;
        Host.Children.Add(Disc(c - rDot, c - rDot, rDot * 2, HubDot, null, 0));
    }

    private void MoveNeedle()
    {
        if (_needleRotate is null)
        {
            return;
        }

        _needleRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = AngleFor(Value),
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        if (_digital is not null)
        {
            _digital.Text = DigitalText();
        }
    }

    private string DigitalText()
    {
        var number = Value.ToString(ValueFormat, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(Unit) ? number : $"{number} {Unit}";
    }

    private double AngleFor(double v)
    {
        var span = Maximum - Minimum;
        var frac = span <= 0 ? 0 : Math.Clamp((v - Minimum) / span, 0, 1);
        return StartAngle + (frac * SweepAngle);
    }

    private static Point OnCircle(double cx, double cy, double r, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        return new Point(cx + (r * Math.Sin(a)), cy - (r * Math.Cos(a)));
    }

    private static bool IsMultiple(double v, double step)
    {
        if (step <= 0)
        {
            return false;
        }

        var n = v / step;
        return Math.Abs(n - Math.Round(n)) < 1e-6;
    }

    private void Text(string text, Point centre, double size, FontWeight weight, Brush brush, string font)
    {
        var t = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = Font(font),
            FontSize = size,
            FontWeight = weight,
            TextAlignment = TextAlignment.Center,
        };
        t.Measure(new Size(300, 300));
        Canvas.SetLeft(t, centre.X - (t.DesiredSize.Width / 2));
        Canvas.SetTop(t, centre.Y - (t.DesiredSize.Height / 2));
        Host.Children.Add(t);
    }

    private static Ellipse Disc(double x, double y, double size, Brush? fill, Brush? stroke, double thickness)
    {
        var e = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill ?? Brushes.Transparent,
            Stroke = stroke,
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    private static Path Arc(double c, double r, double startDeg, double endDeg, Brush brush, double thickness)
    {
        var start = OnCircle(c, c, r, startDeg);
        var end = OnCircle(c, c, r, endDeg);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, Math.Abs(endDeg - startDeg) > 180, SweepDirection.Clockwise, true));
        return new Path
        {
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry([figure]),
        };
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
