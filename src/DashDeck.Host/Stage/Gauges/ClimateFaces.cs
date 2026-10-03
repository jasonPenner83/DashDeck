using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DashDeck.Abstractions;
using DashDeck.Host.Converters;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// A layout element that draws one reading: a gauge, or one of the climate panel's setpoint,
/// levels and indicator elements (ADR-0040). The layout view feeds every one the same way.
/// </summary>
public interface IReadingFace
{
    /// <summary>Draw a new reading — NaN or Unavailable draws as no reading, never a zero.</summary>
    void Show(GaugeReading reading);

    /// <summary>The source is not in its catalog: say so instead of drawing a scale for nothing.</summary>
    void MarkUnknown(string text);

    /// <summary>Where a sensor reading came from (ADR-0039). Faces with no room for it ignore it.</summary>
    void ShowSource(string source);
}

/// <summary>
/// A frosted glass panel (ADR-0040): a translucent tint that is brighter at the top, a sheen across
/// the upper part, a hairline edge that catches the light along the top, and a soft shadow under it.
/// </summary>
/// <remarks>
/// WPF cannot blur what is behind an element cheaply, so the frost is painted: a faint tint over a
/// near-black background reads as glass, and the shadow underneath gives it depth. Elements later
/// in the layout sit on it.
/// </remarks>
public sealed class GlassFace : Grid
{
    public GlassFace(GaugeSpec spec)
    {
        Width = spec.Width;
        Height = spec.Height;

        var r = StageLayout.ParseRadius(spec.Radius) ?? [0, 0, 0, 0];
        var corners = new CornerRadius(r[0], r[1], r[2], r[3]);
        var tint = FaceDraw.ColourOf(spec.Text("tint", "#DDEBFF"), Color.FromRgb(0xDD, 0xEB, 0xFF));
        var opacity = Math.Clamp(spec.Number("opacity", 0.07), 0, 1);
        var sheen = Math.Clamp(spec.Number("sheen", 0.10), 0, 1);
        var shadow = Math.Clamp(spec.Number("shadow", 0.45), 0, 1);
        var edge = spec.Text("edge", "#FFFFFF");

        if (shadow > 0)
        {
            // Under the glass and a little below it, blurred: depth without a hard edge.
            Children.Add(new Border
            {
                CornerRadius = corners,
                Background = FaceDraw.Frozen(Color.FromArgb((byte)(255 * shadow), 0, 0, 0)),
                Margin = new Thickness(4, 10, 4, -6),
                Effect = new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            });
        }

        var body = new Border
        {
            CornerRadius = corners,
            Background = FaceDraw.Vertical(
                (FaceDraw.WithAlpha(tint, opacity * 1.7), 0),
                (FaceDraw.WithAlpha(tint, opacity * 0.55), 1)),
            BorderThickness = new Thickness(edge.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0 : 1),
        };

        if (!edge.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var e = FaceDraw.ColourOf(edge, Colors.White);
            body.BorderBrush = FaceDraw.Vertical((FaceDraw.WithAlpha(e, 0.34), 0), (FaceDraw.WithAlpha(e, 0.10), 0.35), (FaceDraw.WithAlpha(e, 0.04), 1));
        }

        Children.Add(body);

        if (sheen > 0)
        {
            Children.Add(new Border
            {
                CornerRadius = new CornerRadius(corners.TopLeft, corners.TopRight, 0, 0),
                Margin = new Thickness(1, 1, 1, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Height = Math.Max(0, (spec.Height * 0.42) - 1),
                Background = FaceDraw.Vertical((FaceDraw.WithAlpha(Colors.White, sheen), 0), (FaceDraw.WithAlpha(Colors.White, 0), 1)),
                IsHitTestVisible = false,
            });
        }
    }
}

/// <summary>
/// A set temperature drawn large, with a thin glowing arc showing where it sits between
/// <c>min</c> and <c>max</c> and a bright point at the value (ADR-0040).
/// </summary>
public sealed class SetpointFace : Canvas, IReadingFace
{
    private readonly GaugeSpec _spec;
    private readonly double _cx;
    private readonly double _cy;
    private readonly double _r;
    private readonly double _sweep;
    private readonly double _thickness;
    private readonly Canvas _dynamic = new();
    private readonly TextBlock _value;
    private readonly TextBlock _unit;
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = QualityPalette.Unavailable };
    private bool _unknown;
    private string _unknownText = "UNKNOWN SIGNAL";

    public SetpointFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;

        var labelSize = spec.Number("labelSize", 12);
        _sweep = Math.Clamp(spec.Number("sweep", 240), 30, 340);
        _thickness = spec.Number("thickness", 4);
        _r = Math.Max(10, Math.Min(spec.Width / 2, (spec.Height - labelSize - 12) / (1 + Math.Max(0, -Math.Cos(_sweep / 2 * Math.PI / 180)))) - _thickness - 8);
        _cx = spec.Width / 2;
        _cy = _r + _thickness + 6;

        if (spec.Flag("showArc", true))
        {
            var track = FaceDraw.Arc(_cx, _cy, _r, -_sweep / 2, _sweep / 2, _thickness);
            GaugeFace.Paint(track, Shape.StrokeProperty, spec.Text("trackColour", ""), "#1FFFFFFF");
            Children.Add(track);

            // The ends of the range, small, under the arc's tips.
            AddEnd(spec.Min, -_sweep / 2);
            AddEnd(spec.Max, _sweep / 2);
        }

        Children.Add(_dynamic);

        _value = FaceDraw.Label(spec.Text("valueColour", ""), "@textHigh", spec.Number("valueSize", Math.Max(18, _r * 0.62)), "ui", FaceDraw.Weight(spec, "valueWeight", FontWeights.Light));
        _unit = FaceDraw.Label(spec.Text("valueColour", ""), "@textHigh", spec.Number("valueSize", Math.Max(18, _r * 0.62)) * 0.45, "ui", FaceDraw.Weight(spec, "valueWeight", FontWeights.Light));
        Children.Add(_value);
        Children.Add(_unit);

        if (spec.Label.Length > 0)
        {
            var caption = FaceDraw.Label(spec.Text("labelColour", ""), "@caption", labelSize, "mono", FaceDraw.Weight(spec, "labelWeight", FontWeights.SemiBold));
            caption.Text = spec.Label;
            caption.Width = spec.Width;
            caption.TextAlignment = TextAlignment.Center;
            FaceDraw.Place(caption, 0, spec.Height - labelSize - 6);
            Children.Add(caption);
        }

        FaceDraw.Place(_dot, spec.Width - 12, 4);
        Children.Add(_dot);

        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void MarkUnknown(string text)
    {
        _unknown = true;
        _unknownText = text;
        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void ShowSource(string source)
    {
    }

    public void Show(GaugeReading reading)
    {
        _dot.Fill = _unknown ? QualityPalette.Fault : QualityPalette.For(reading.Quality);
        _dynamic.Children.Clear();

        var showing = !_unknown && reading.HasValue;
        var dim = reading.Quality is SignalQuality.Stale ? 0.45 : 1;
        _dynamic.Opacity = dim;
        _value.Opacity = showing ? dim : 0.5;
        _unit.Opacity = _value.Opacity;

        _value.Text = _unknown ? _unknownText : showing ? reading.Value.ToString(_spec.Format, CultureInfo.InvariantCulture) : "– –";
        _value.FontSize = _unknown ? 13 : _spec.Number("valueSize", Math.Max(18, _r * 0.62));
        _unit.Text = showing ? _spec.Unit : "";
        CentreValue();

        if (!showing || !_spec.Flag("showArc", true))
        {
            return;
        }

        var span = _spec.Max - _spec.Min;
        var fraction = span <= 0 ? 0 : Math.Clamp((reading.Value - _spec.Min) / span, 0, 1);
        var end = (-_sweep / 2) + (fraction * _sweep);
        var colour = _spec.Text("arcColour", "@accent");
        var glow = _spec.Text("glow", colour);

        if (fraction > 0.004)
        {
            var fill = FaceDraw.Arc(_cx, _cy, _r, -_sweep / 2, end, _thickness);
            GaugeFace.Paint(fill, Shape.StrokeProperty, colour, "@accent");
            FaceDraw.Glow(fill, glow, 14);
            _dynamic.Children.Add(fill);
        }

        // The bright point at the value: where the set temperature sits on the range.
        var knob = _thickness * 2.6;
        var at = FaceDraw.OnCircle(_cx, _cy, _r, end);
        var point = new Ellipse { Width = knob, Height = knob, Fill = Brushes.White };
        FaceDraw.Glow(point, glow, 12);
        FaceDraw.Place(point, at.X - (knob / 2), at.Y - (knob / 2));
        _dynamic.Children.Add(point);
    }

    private void CentreValue()
    {
        _value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _unit.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _value.DesiredSize.Width + _unit.DesiredSize.Width;
        var left = _cx - (width / 2);
        var top = _cy - (_value.DesiredSize.Height / 2);
        FaceDraw.Place(_value, left, top);

        // The degree sign rides high beside the number, as on a cluster.
        FaceDraw.Place(_unit, left + _value.DesiredSize.Width, top + (_value.DesiredSize.Height * 0.08));
    }

    private void AddEnd(double value, double angle)
    {
        var end = FaceDraw.Label(_spec.Text("labelColour", ""), "@caption", 10, "mono", FontWeights.Normal);
        end.Text = value.ToString("0.#", CultureInfo.InvariantCulture);
        end.Opacity = 0.7;
        var at = FaceDraw.OnCircle(_cx, _cy, _r + _thickness + 9, angle);
        end.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        FaceDraw.Place(end, at.X - (end.DesiredSize.Width / 2), at.Y - (end.DesiredSize.Height / 2) + 6);
        Children.Add(end);
    }
}

/// <summary>
/// A row of steps lit up to the value — fan speed, or seat heat and cooling (ADR-0040). Below zero
/// lights in <c>negativeColour</c>, so one element shows a seat that heats and cools.
/// </summary>
public sealed class LevelsFace : Canvas, IReadingFace
{
    private readonly GaugeSpec _spec;
    private readonly int _steps;
    private readonly List<Shape> _cells = [];
    private readonly TextBlock? _value;
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Fill = QualityPalette.Unavailable };
    private bool _unknown;
    private string _unknownText = "UNKNOWN SIGNAL";

    public LevelsFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;
        _steps = (int)Math.Clamp(spec.Number("steps", 7), 1, 20);

        var labelSize = spec.Number("labelSize", 12);
        var head = spec.Label.Length > 0 || spec.Flag("showValue", true) ? labelSize + 8 : 0;

        if (spec.Label.Length > 0)
        {
            var caption = FaceDraw.Label(spec.Text("labelColour", ""), "@caption", labelSize, "mono", FaceDraw.Weight(spec, "labelWeight", FontWeights.SemiBold));
            caption.Text = spec.Label;
            FaceDraw.Place(caption, 0, 0);
            Children.Add(caption);
        }

        if (spec.Flag("showValue", true))
        {
            _value = FaceDraw.Label(spec.Text("labelColour", ""), "@caption", labelSize, "mono", FaceDraw.Weight(spec, "labelWeight", FontWeights.SemiBold));
            _value.Width = spec.Width - 14;
            _value.TextAlignment = TextAlignment.Right;
            FaceDraw.Place(_value, 0, 0);
            Children.Add(_value);
        }

        var gap = spec.Number("gap", 6);
        var dots = spec.Text("shape", "bars").Equals("dots", StringComparison.OrdinalIgnoreCase);
        var area = Math.Max(4, spec.Height - head);
        var cell = Math.Max(2, (spec.Width - (gap * (_steps - 1))) / _steps);

        for (var i = 0; i < _steps; i++)
        {
            Shape shape;
            if (dots)
            {
                var d = Math.Min(cell, area);
                shape = new Ellipse { Width = d, Height = d };
                FaceDraw.Place(shape, (i * (cell + gap)) + ((cell - d) / 2), head + ((area - d) / 2));
            }
            else
            {
                // Rising bars: the first a third of the height, the last all of it.
                var h = area * (_steps == 1 ? 1 : 0.34 + (0.66 * i / (_steps - 1)));
                shape = new Rectangle { Width = cell, Height = h, RadiusX = Math.Min(3, cell / 2), RadiusY = Math.Min(3, cell / 2) };
                FaceDraw.Place(shape, i * (cell + gap), head + area - h);
            }

            _cells.Add(shape);
            Children.Add(shape);
        }

        FaceDraw.Place(_dot, spec.Width - 8, 3);
        Children.Add(_dot);

        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void MarkUnknown(string text)
    {
        _unknown = true;
        _unknownText = text;
        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void ShowSource(string source)
    {
    }

    public void Show(GaugeReading reading)
    {
        _dot.Fill = _unknown ? QualityPalette.Fault : QualityPalette.For(reading.Quality);
        var showing = !_unknown && reading.HasValue;
        var (lit, negative) = showing ? ClimateReadings.Lit(reading.Value, _spec.Min, _spec.Max, _steps) : (0, false);
        var dim = reading.Quality is SignalQuality.Stale ? 0.45 : 1;

        if (_value is not null)
        {
            _value.Text = _unknown ? _unknownText
                : !showing ? "–"
                : ClimateReadings.LevelText(reading.Value, _spec.Format, _spec.Text("positiveText", ""), _spec.Text("negativeText", ""));
            _value.Opacity = showing ? dim : 0.5;
        }

        var litColour = negative ? _spec.Text("negativeColour", "#6CC8FF") : _spec.Text("litColour", "@accent");

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (i < lit)
            {
                GaugeFace.Paint(cell, Shape.FillProperty, litColour, "@accent");
                FaceDraw.Glow(cell, litColour, 10);
                cell.Opacity = dim;
            }
            else
            {
                GaugeFace.Paint(cell, Shape.FillProperty, _spec.Text("unlitColour", ""), "#1CFFFFFF");
                cell.Effect = null;
                cell.Opacity = showing ? 1 : 0.5;
            }
        }
    }
}

/// <summary>
/// A pill that lights when its signal is on — A/C, AUTO, RECIRC, a defroster, an airflow mode
/// (ADR-0040). Off is outlined glass; not known is dimmed with a dash, never shown as off.
/// </summary>
public sealed class IndicatorFace : Grid, IReadingFace
{
    private readonly GaugeSpec _spec;
    private readonly Border _pill;
    private readonly TextBlock _text;
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Fill = QualityPalette.Unavailable };
    private readonly bool _textOnly;
    private bool _unknown;

    public IndicatorFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;

        _text = new TextBlock
        {
            Text = spec.Label,
            FontSize = spec.Number("fontSize", Math.Clamp(spec.Height * 0.32, 10, 20)),
            FontWeight = FaceDraw.Weight(spec, "labelWeight", FontWeights.SemiBold),
            HorizontalAlignment = spec.Text("align", "center").ToLowerInvariant() switch
            {
                "left" => HorizontalAlignment.Left,
                "right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

        _textOnly = spec.Text("style", "pill").Equals("text", StringComparison.OrdinalIgnoreCase);
        _pill = new Border
        {
            CornerRadius = new CornerRadius(spec.Number("radius", spec.Height / 2)),
            BorderThickness = new Thickness(_textOnly ? 0 : 1),
            Child = _text,
        };

        Children.Add(_pill);

        _dot.HorizontalAlignment = HorizontalAlignment.Right;
        _dot.VerticalAlignment = VerticalAlignment.Top;
        _dot.Margin = new Thickness(0, 5, Math.Max(8, spec.Number("radius", spec.Height / 2) * 0.55), 0);
        Children.Add(_dot);

        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void MarkUnknown(string text)
    {
        _unknown = true;
        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void ShowSource(string source)
    {
    }

    public void Show(GaugeReading reading)
    {
        _dot.Fill = _unknown ? QualityPalette.Fault : QualityPalette.For(reading.Quality);
        var showing = !_unknown && reading.HasValue;
        var on = showing && ClimateReadings.IsOn(_spec, reading.Value);
        var lit = _spec.Text("litColour", "@accent");

        _text.Text = showing ? _spec.Label : $"{_spec.Label} –";

        if (_textOnly)
        {
            // Just the word: lit in its colour when on, quiet grey when off. No pill, no glow.
            _pill.Background = Brushes.Transparent;
            _pill.Effect = null;
            GaugeFace.Paint(_text, TextBlock.ForegroundProperty, on ? lit : _spec.Text("unlitColour", ""), on ? "@accent" : "#5C6670");
            Opacity = !showing ? 0.45 : reading.Quality is SignalQuality.Stale ? 0.5 : 1;
            return;
        }

        if (on)
        {
            GaugeFace.Paint(_pill, Border.BackgroundProperty, lit, "@accent");
            GaugeFace.Paint(_pill, Border.BorderBrushProperty, lit, "@accent");
            GaugeFace.Paint(_text, TextBlock.ForegroundProperty, _spec.Text("litText", ""), "#05080C");
            FaceDraw.Glow(_pill, lit, 16);
        }
        else
        {
            _pill.Background = FaceDraw.Frozen(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
            GaugeFace.Paint(_pill, Border.BorderBrushProperty, _spec.Text("unlitColour", ""), "#33FFFFFF");
            GaugeFace.Paint(_text, TextBlock.ForegroundProperty, _spec.Text("unlitColour", ""), "#8A97A4");
            _pill.Effect = null;
        }

        Opacity = !showing ? 0.4 : reading.Quality is SignalQuality.Stale ? 0.45 : 1;
    }
}

/// <summary>Drawing helpers the climate faces share.</summary>
internal static class FaceDraw
{
    /// <summary>
    /// A weight a layout names for its type — <c>"thin"</c>, <c>"light"</c>, <c>"regular"</c>,
    /// <c>"medium"</c>, <c>"semibold"</c>, <c>"bold"</c> — or the face's own default.
    /// </summary>
    public static FontWeight Weight(GaugeSpec spec, string part, FontWeight fallback) =>
        spec.Text(part, "").Trim().ToLowerInvariant() switch
        {
            "thin" => FontWeights.Thin,
            "extralight" => FontWeights.ExtraLight,
            "light" => FontWeights.Light,
            "semilight" => FontWeights.Light,
            "regular" or "normal" => FontWeights.Normal,
            "medium" => FontWeights.Medium,
            "semibold" => FontWeights.SemiBold,
            "bold" => FontWeights.Bold,
            _ => fallback,
        };

    public static TextBlock Label(string colour, string fallback, double size, string font, FontWeight weight)
    {
        var t = new TextBlock { FontSize = size, FontWeight = weight };
        t.SetResourceReference(TextBlock.FontFamilyProperty, font == "ui" ? "UiFont" : "MonoFont");
        GaugeFace.Paint(t, TextBlock.ForegroundProperty, colour, fallback);
        return t;
    }

    public static void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    public static Point OnCircle(double cx, double cy, double r, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        return new Point(cx + (r * Math.Sin(a)), cy - (r * Math.Cos(a)));
    }

    public static Path Arc(double cx, double cy, double r, double startDeg, double endDeg, double thickness)
    {
        var figure = new PathFigure { StartPoint = OnCircle(cx, cy, r, startDeg), IsClosed = false };
        figure.Segments.Add(new ArcSegment(OnCircle(cx, cy, r, endDeg), new Size(r, r), 0, Math.Abs(endDeg - startDeg) > 180, SweepDirection.Clockwise, true));
        return new Path
        {
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry([figure]),
        };
    }

    /// <summary>A soft glow in a layout colour, or none for <c>"none"</c>.</summary>
    public static void Glow(UIElement element, string colour, double blur)
    {
        if (colour.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            element.Effect = null;
            return;
        }

        element.Effect = GaugeFace.Resolve(colour) is SolidColorBrush brush
            ? new DropShadowEffect { Color = brush.Color, BlurRadius = blur, ShadowDepth = 0, Opacity = 0.85 }
            : null;
    }

    /// <summary>A layout colour as a colour now — <c>@token</c> resolved against the current theme.</summary>
    public static Color ColourOf(string colour, Color fallback) =>
        GaugeFace.Resolve(colour) is SolidColorBrush brush ? brush.Color : fallback;

    public static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);

    public static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public static LinearGradientBrush Vertical(params (Color Colour, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        foreach (var (colour, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(colour, offset));
        }

        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// A warning light (ADR-0041): an icon lit in its colour, with a glow, when its signal says so;
/// faint when off; fainter, with a grey quality dot, when the truck has not said — a light that
/// is not known is never drawn as off with confidence.
/// </summary>
public sealed class WarningFace : Grid, IReadingFace
{
    private readonly GaugeSpec _spec;
    private readonly Path _icon;
    private readonly Ellipse _dot = new() { Width = 5, Height = 5, Fill = QualityPalette.Unavailable };
    private bool _unknown;

    public WarningFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;

        var icon = WarningIcons.Find(spec.Text("icon", "")) ?? WarningIcons.Find("brake")!;
        // Path markup carries its own fill rule: F0 cuts holes even-odd (a window in a door), F1 fills.
        // A layout's own data that does not parse falls back to the brake symbol rather than nothing.
        Geometry geometry;
        try
        {
            geometry = Geometry.Parse((icon.EvenOdd ? "F0 " : "F1 ") + icon.Path);
        }
        catch (FormatException)
        {
            geometry = Geometry.Parse("F0 " + WarningIcons.Find("brake")!.Path);
        }

        _icon = new Path { Data = geometry, Stretch = Stretch.Uniform, Margin = new Thickness(Math.Max(1, spec.Width * 0.06)) };
        Children.Add(_icon);

        _dot.HorizontalAlignment = HorizontalAlignment.Right;
        _dot.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_dot);

        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    /// <summary>True while lit — for Describe().</summary>
    public bool IsLit { get; private set; }

    public void MarkUnknown(string text)
    {
        _unknown = true;
        Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
    }

    public void ShowSource(string source)
    {
    }

    public void Show(GaugeReading reading)
    {
        _dot.Fill = _unknown ? QualityPalette.Fault : QualityPalette.For(reading.Quality);

        // The dot only where it says something: a lit or dark light that is Live needs no badge.
        _dot.Visibility = !_unknown && reading.Quality is SignalQuality.Live ? Visibility.Collapsed : Visibility.Visible;

        var showing = !_unknown && reading.HasValue;
        IsLit = showing && ClimateReadings.IsOn(_spec, reading.Value);
        var lit = _spec.Text("litColour", "#FFB020");

        if (IsLit)
        {
            GaugeFace.Paint(_icon, Shape.FillProperty, lit, "#FFB020");
            FaceDraw.Glow(_icon, lit, 12);
            _icon.Opacity = reading.Quality is SignalQuality.Stale ? 0.5 : 1;
        }
        else
        {
            GaugeFace.Paint(_icon, Shape.FillProperty, _spec.Text("unlitColour", ""), "#2EFFFFFF");
            _icon.Effect = null;
            _icon.Opacity = showing ? 1 : 0.45;
        }
    }
}
