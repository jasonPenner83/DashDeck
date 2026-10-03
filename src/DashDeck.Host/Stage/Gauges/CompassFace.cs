using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DashDeck.Abstractions;
using DashDeck.Host.Converters;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// A compass rose on the stage (ADR-0039): ticks and letters round a ring, the heading and its
/// compass point in the middle, and where the heading came from underneath.
/// </summary>
/// <remarks>
/// The old COMPASS screen's rose, drawn from a layout element so it can be sized, placed and
/// coloured like any gauge. Two modes: <c>rose</c> turns the card under a fixed marker, as a ship's
/// compass does; <c>needle</c> keeps north up and turns a needle instead. Proportions are the old
/// 320 px rose's, scaled to the element.
/// <para>
/// <b>No heading, no rotation.</b> With nothing usable the card stays where it was last put, the
/// number reads <c>———</c> and the source line says why — never a confident north.
/// </para>
/// </remarks>
public sealed class CompassFace : Canvas
{
    private readonly GaugeSpec _spec;
    private readonly RotateTransform _turn = new();
    private readonly double _scale;
    private TextBlock? _value;
    private TextBlock? _cardinal;
    private TextBlock? _source;
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Fill = QualityPalette.Unavailable };
    private readonly bool _needle;

    public CompassFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;
        _needle = spec.Text("mode", "rose").Equals("needle", StringComparison.OrdinalIgnoreCase);

        var showSource = spec.Flag("showSource", true);
        var diameter = Math.Max(40, Math.Min(spec.Width, spec.Height - (showSource ? 22 : 0)));
        _scale = diameter / 320;

        Build(diameter, showSource);
    }

    /// <summary>The heading being shown, after smoothing, or NaN.</summary>
    public double Heading { get; private set; } = double.NaN;

    /// <summary>Draw a heading (already smoothed), how far to trust it, and where it came from.</summary>
    public void Show(double heading, SignalQuality quality, string source)
    {
        Heading = heading;
        var has = !double.IsNaN(heading);

        if (has)
        {
            _turn.Angle = _needle ? heading : -heading;
        }

        if (_value is not null)
        {
            _value.Text = has ? heading.ToString("000", CultureInfo.CurrentCulture) : "———";
            _value.Opacity = has ? 1 : 0.5;
        }

        if (_cardinal is not null)
        {
            _cardinal.Text = SensorMath.Cardinal(heading);
        }

        if (_source is not null)
        {
            _source.Text = source;
            _source.Foreground = QualityPalette.For(quality);
        }

        _dot.Fill = QualityPalette.For(has ? quality : SignalQuality.Unavailable);
    }

    private void Build(double d, bool showSource)
    {
        var cx = _spec.Width / 2;
        var cy = d / 2;
        var r = d / 2;
        var s = _scale;

        // The ring.
        var ringColour = _spec.Text("ringColour", "@hairlineStrong");
        if (!ringColour.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var ring = new Ellipse { Width = d - (20 * s), Height = d - (20 * s), StrokeThickness = 1 };
            GaugeFace.Paint(ring, Shape.StrokeProperty, ringColour, "@hairlineStrong");
            Place(ring, cx - (ring.Width / 2), cy - (ring.Height / 2));
            Children.Add(ring);
        }

        // The card: ticks and letters, turned as a whole in rose mode.
        var card = new Canvas { Width = d, Height = d, RenderTransformOrigin = new Point(0.5, 0.5) };
        if (!_needle)
        {
            card.RenderTransform = _turn;
        }

        var step = Math.Clamp(_spec.Number("tickStep", 5), 1, 90);
        for (var degrees = 0.0; degrees < 360 - 1e-6; degrees += step)
        {
            var isCardinal = IsMultiple(degrees, 90);
            var isMajor = IsMultiple(degrees, 45);
            var length = (isCardinal ? 20 : isMajor ? 14 : 7) * s;
            var outer = r - (12 * s);
            var inner = outer - length;
            var a = (degrees - 90) * Math.PI / 180;

            var tick = new Line
            {
                X1 = r + (Math.Cos(a) * inner),
                Y1 = r + (Math.Sin(a) * inner),
                X2 = r + (Math.Cos(a) * outer),
                Y2 = r + (Math.Sin(a) * outer),
                StrokeThickness = isMajor ? 2 : 1,
                SnapsToDevicePixels = true,
            };

            if (isCardinal)
            {
                GaugeFace.Paint(tick, Shape.StrokeProperty, _spec.Text("cardinalTickColour", ""), "@accent");
            }
            else if (isMajor)
            {
                GaugeFace.Paint(tick, Shape.StrokeProperty, _spec.Text("majorTickColour", ""), "@textLow");
            }
            else
            {
                GaugeFace.Paint(tick, Shape.StrokeProperty, _spec.Text("minorTickColour", ""), "@textFaint");
            }

            card.Children.Add(tick);
        }

        if (_spec.Flag("showLetters", true))
        {
            string[] letters = ["N", "E", "S", "W"];
            var northSize = _spec.Number("letterSize", 24 * s);

            for (var i = 0; i < letters.Length; i++)
            {
                var label = new TextBlock { Text = letters[i], FontSize = Math.Max(6, i == 0 ? northSize : northSize * 0.75) };
                label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                GaugeFace.Paint(label, TextBlock.ForegroundProperty,
                    i == 0 ? _spec.Text("northColour", "") : _spec.Text("letterColour", ""),
                    i == 0 ? "@accent" : "@textMid");

                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var a = ((i * 90) - 90) * Math.PI / 180;
                var radius = r - (54 * s);
                Place(label, r + (Math.Cos(a) * radius) - (label.DesiredSize.Width / 2), r + (Math.Sin(a) * radius) - (label.DesiredSize.Height / 2));
                card.Children.Add(label);
            }
        }

        Place(card, cx - r, 0);
        Children.Add(card);

        // The marker: a fixed triangle at the top in rose mode; a needle that turns in needle mode.
        if (_needle)
        {
            var needle = new Path
            {
                Data = Geometry.Parse(string.Create(CultureInfo.InvariantCulture,
                    $"M{r},{r - (r * 0.62)} L{r + (7 * s)},{r - (r * 0.30)} L{r - (7 * s)},{r - (r * 0.30)} Z")),
                Width = d,
                Height = d,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _turn,
            };
            GaugeFace.Paint(needle, Shape.FillProperty, _spec.Text("markerColour", ""), "@accent");
            Place(needle, cx - r, 0);
            Children.Add(needle);
        }
        else
        {
            var marker = new Path
            {
                Data = Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M0,0 L{16 * s},0 L{8 * s},{13 * s} Z")),
            };
            GaugeFace.Paint(marker, Shape.FillProperty, _spec.Text("markerColour", ""), "@accent");
            Place(marker, cx - (8 * s), -2 * s);
            Children.Add(marker);
        }

        // The number and the compass point, in the middle.
        var middle = new StackPanel { Width = _spec.Width };
        if (_spec.Flag("showValue", true))
        {
            _value = new TextBlock
            {
                FontSize = _spec.Number("valueSize", 62 * s),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _value.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            GaugeFace.Paint(_value, TextBlock.ForegroundProperty, _spec.Text("valueColour", ""), "@textHigh");
            middle.Children.Add(_value);
        }

        if (_spec.Flag("showCardinal", true))
        {
            _cardinal = new TextBlock
            {
                FontSize = Math.Max(8, 24 * s),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -6 * s, 0, 0),
            };
            _cardinal.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            GaugeFace.Paint(_cardinal, TextBlock.ForegroundProperty, _spec.Text("cardinalColour", ""), "@accent");
            middle.Children.Add(_cardinal);
        }

        if (middle.Children.Count > 0)
        {
            // Measured with sample text, so the block stays centred as the digits change.
            if (_value is not null)
            {
                _value.Text = "000";
            }

            if (_cardinal is not null)
            {
                _cardinal.Text = "NE";
            }

            middle.Measure(new Size(_spec.Width, double.PositiveInfinity));
            Place(middle, 0, cy - (middle.DesiredSize.Height / 2));
            Children.Add(middle);
        }

        if (showSource)
        {
            _source = new TextBlock { FontSize = 12, Width = _spec.Width, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _source.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            Place(_source, 0, d + 4);
            Children.Add(_source);
        }

        Place(_dot, _spec.Width - 13, 4);
        Children.Add(_dot);

        Show(double.NaN, SignalQuality.Unavailable, "");
    }

    private static bool IsMultiple(double v, double step) => Math.Abs((v / step) - Math.Round(v / step)) < 1e-6;

    private static void Place(UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
    }
}
