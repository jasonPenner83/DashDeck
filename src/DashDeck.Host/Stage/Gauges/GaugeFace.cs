using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DashDeck.Abstractions;
using DashDeck.Host.Converters;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// Draws one gauge from its <see cref="GaugeSpec"/> (ADR-0037): a dial, an arc, a bar, an LCARS
/// bar, or a plain number.
/// </summary>
/// <remarks>
/// Custom-drawn, like the <c>RoundGauge</c> it replaces: the static parts are laid down once in
/// <see cref="Build"/>, and only the moving parts change with each reading — the dial's needle
/// eases toward the value; the others redraw their fill, which at the one to four readings a
/// second a gauge gets is nothing.
/// <para>
/// <b>A gauge with no reading draws no needle and no fill, and says so.</b> Stale draws dimmed.
/// A small dot in the quality colour sits in the corner of every gauge — the same four colours as
/// the cards, never themeable (ADR-0013). Colours written as <c>@token</c> follow the theme and its
/// night dimming live; colours written as <c>#RRGGBB</c> are exactly that, day and night.
/// </para>
/// </remarks>
public sealed class GaugeFace : Canvas
{
    // The F-150 cluster's own colours — the dial's defaults. Ice-blue on matte black.
    private const string FordNeedle = "#B9F1F7";
    private const string FordGlow = "#2FD4E6";
    private const string FordFace = "#08090A";
    private const string FordTick = "#EDEFF2";
    private const string FordMinorTick = "#7C848C";
    private const string FordLabel = "#AEB6BE";

    private static readonly Brush ChromeBezel = Gradient(
        ("#EEF2F5", 0.00), ("#AEB6BD", 0.14), ("#5A6067", 0.34), ("#2C3034", 0.50),
        ("#4C525A", 0.66), ("#9BA3AB", 0.85), ("#D7DCE0", 1.00));

    private readonly GaugeSpec _spec;
    private readonly Canvas _dynamic = new();
    private RotateTransform? _needle;
    private UIElement? _needleShape;
    private TextBlock? _value;
    private Ellipse? _dot;
    private GaugeReading _reading = new(double.NaN, SignalQuality.Unavailable);
    private bool _unknown;

    public GaugeFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;
        ClipToBounds = false;
        Build();
    }

    /// <summary>The source signal is not in the catalog: say so instead of drawing a scale for nothing.</summary>
    public void MarkUnknown()
    {
        _unknown = true;
        Show(_reading);
    }

    /// <summary>Draw a new reading.</summary>
    public void Show(GaugeReading reading)
    {
        _reading = reading;

        if (_dot is not null)
        {
            _dot.Fill = _unknown ? QualityPalette.Fault : QualityPalette.For(reading.Quality);
        }

        _dynamic.Opacity = reading.Quality is SignalQuality.Stale ? 0.45 : 1;

        if (_value is not null)
        {
            _value.Opacity = _dynamic.Opacity;
            _value.Text = _unknown ? "UNKNOWN SIGNAL"
                : !reading.HasValue ? "NO DATA"
                : string.IsNullOrEmpty(_spec.Unit) || _spec.Style is GaugeStyle.LcarsBar
                    ? Number(reading.Value)
                    : $"{Number(reading.Value)} {_spec.Unit}";
        }

        switch (_spec.Style)
        {
            case GaugeStyle.Dial:
                MoveNeedle();
                break;
            case GaugeStyle.Arc:
                DrawArcFill();
                break;
            case GaugeStyle.Bar:
                DrawBarFill();
                break;
            case GaugeStyle.LcarsBar:
                DrawLcarsFill();
                break;
        }
    }

    private string Number(double v) => v.ToString(_spec.Format, CultureInfo.InvariantCulture);

    private bool Showing => !_unknown && _reading.HasValue;

    private double Fraction => Showing ? Math.Clamp((_reading.Value - _spec.Min) / (_spec.Max - _spec.Min), 0, 1) : 0;

    // ── Building ──────────────────────────────────────────────────────────────

    private void Build()
    {
        Children.Clear();

        switch (_spec.Style)
        {
            case GaugeStyle.Dial:
                BuildDial();
                break;
            case GaugeStyle.Arc:
                BuildArc();
                break;
            case GaugeStyle.Bar:
                BuildBar();
                break;
            case GaugeStyle.LcarsBar:
                BuildLcars();
                break;
            default:
                BuildDigital();
                break;
        }

        Children.Add(_dynamic);

        // The quality dot, in the top-right corner of every gauge, on top of everything.
        _dot = new Ellipse { Width = 9, Height = 9, Fill = QualityPalette.Unavailable };
        Place(_dot, _spec.Width - 13, 4);
        Children.Add(_dot);

        Show(_reading);
    }

    // ── Dial ──────────────────────────────────────────────────────────────────

    private double StartAngle => _spec.Number("startAngle", -135);

    private double Sweep => _spec.Number("sweep", 270);

    private double AngleFor(double v) =>
        StartAngle + (Math.Clamp((v - _spec.Min) / (_spec.Max - _spec.Min), 0, 1) * Sweep);

    private void BuildDial()
    {
        var d = Math.Min(_spec.Width, _spec.Height);
        var big = d > 200;
        var cx = _spec.Width / 2;
        var cy = _spec.Height / 2;
        var left = cx - (d / 2);
        var top = cy - (d / 2);

        var bezel = _spec.Text("bezel", "chrome");
        var bezelW = bezel == "none" ? 0 : big ? 15.0 : 9.0;

        if (bezel == "chrome")
        {
            Children.Add(Disc(left, top, d, ChromeBezel, null, 0));
        }
        else if (bezel == "ring")
        {
            var ring = Disc(left, top, d, null, null, bezelW / 2);
            Paint(ring, Shape.StrokeProperty, _spec.Text("bezelColour", ""), "@hairlineStrong");
            Children.Add(ring);
        }

        var faceSpec = _spec.Text("face", FordFace);
        if (!faceSpec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var face = Disc(left + bezelW, top + bezelW, d - (bezelW * 2), null, null, 0);
            Paint(face, Shape.FillProperty, faceSpec, FordFace);
            Children.Add(face);
        }

        var r = (d / 2) - bezelW - (big ? 8 : 6);
        var tickLength = _spec.Number("tickLength", big ? 20 : 12);

        foreach (var zone in _spec.Zones.Where(z => StageLayout.IsColour(z.Colour) && z.To > z.From))
        {
            var arc = Arc(cx, cy, r - 3, AngleFor(zone.From), AngleFor(Math.Min(zone.To, _spec.Max)), big ? 6 : 4);
            Paint(arc, Shape.StrokeProperty, zone.Colour, zone.Colour);
            Children.Add(arc);
        }

        Ticks(cx, cy, r, tickLength, big);

        if (_spec.Flag("numerals", big) && _spec.MajorTick > 0)
        {
            var divisor = _spec.Number("numeralDivisor", 1);
            var rNum = r - tickLength - (big ? 22 : 13);

            for (var v = _spec.Min; v <= _spec.Max + 1e-6; v += _spec.MajorTick)
            {
                var text = Label((v / divisor).ToString("0.##", CultureInfo.InvariantCulture), big ? 24 : 13, "ui",
                    _spec.Text("numeralColour", FordTick), FordTick, FontWeights.SemiBold);
                Centre(text, OnCircle(cx, cy, rNum, AngleFor(v)));
            }
        }

        if (_spec.Flag("showLabel", true) && _spec.Label.Length > 0)
        {
            var caption = Label(_spec.Label, _spec.Number("labelSize", big ? 15 : 10), "mono",
                _spec.Text("labelColour", FordLabel), FordLabel, FontWeights.SemiBold);
            Centre(caption, new Point(cx, cy + (d * (big ? 0.14 : 0.13))));
        }

        if (_spec.Flag("showValue", true))
        {
            _value = Label("", _spec.Number("valueSize", big ? 30 : 17), "ui",
                _spec.Text("valueColour", FordTick), FordTick, FontWeights.Bold);
            _value.Width = _spec.Width;
            _value.TextAlignment = TextAlignment.Center;
            Place(_value, 0, cy + (d * (big ? 0.2 : 0.24)));
        }

        // Needle: a thin spear pivoted at the centre, lit with a glow. In the dynamic layer so a
        // stale reading dims it with everything else that moves.
        var tip = r - (big ? 8 : 4);
        var half = _spec.Number("needleWidth", big ? 8 : 5.2) / 2;
        var tail = big ? 20.0 : 11.0;
        var needle = new Polygon
        {
            Points = [new Point(cx, cy - tip), new Point(cx + half, cy), new Point(cx, cy + tail), new Point(cx - half, cy)],
            RenderTransform = _needle = new RotateTransform(StartAngle, cx, cy),
        };
        Paint(needle, Shape.FillProperty, _spec.Text("needleColour", FordNeedle), FordNeedle);

        var glow = _spec.Text("needleGlow", FordGlow);
        if (!glow.Equals("none", StringComparison.OrdinalIgnoreCase) && Resolve(glow) is SolidColorBrush glowBrush)
        {
            needle.Effect = new DropShadowEffect { Color = glowBrush.Color, BlurRadius = big ? 16 : 9, ShadowDepth = 0, Opacity = 0.9 };
        }

        _needleShape = needle;
        _dynamic.Children.Add(needle);

        // The hub over the needle's root, so the needle looks pinned to a spindle.
        var rHub = big ? 13.0 : 8.0;
        _dynamic.Children.Add(Disc(cx - rHub, cy - rHub, rHub * 2, Frozen("#16181A"), Frozen("#4A4E52"), big ? 2 : 1.5));
        var dot = Disc(cx - (rHub / 3), cy - (rHub / 3), rHub * 2 / 3, null, null, 0);
        Paint(dot, Shape.FillProperty, _spec.Text("hubColour", FordGlow), FordGlow);
        _dynamic.Children.Add(dot);
    }

    private void Ticks(double cx, double cy, double r, double majorLength, bool big)
    {
        if (_spec.MinorTick > 0)
        {
            for (var v = _spec.Min; v <= _spec.Max + 1e-6; v += _spec.MinorTick)
            {
                if (_spec.MajorTick > 0 && IsMultiple(v - _spec.Min, _spec.MajorTick))
                {
                    continue;
                }

                Children.Add(TickLine(cx, cy, r, majorLength * 0.55, AngleFor(v), 1, _spec.Text("minorTickColour", FordMinorTick), FordMinorTick));
            }
        }

        if (_spec.MajorTick > 0)
        {
            for (var v = _spec.Min; v <= _spec.Max + 1e-6; v += _spec.MajorTick)
            {
                Children.Add(TickLine(cx, cy, r, majorLength, AngleFor(v), big ? 3 : 2, _spec.Text("tickColour", FordTick), FordTick));
            }
        }
    }

    private Line TickLine(double cx, double cy, double r, double length, double angle, double width, string colour, string fallback)
    {
        var outer = OnCircle(cx, cy, r, angle);
        var inner = OnCircle(cx, cy, r - length, angle);
        var line = new Line
        {
            X1 = outer.X, Y1 = outer.Y, X2 = inner.X, Y2 = inner.Y,
            StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Paint(line, Shape.StrokeProperty, colour, fallback);
        return line;
    }

    private void MoveNeedle()
    {
        if (_needle is null || _needleShape is null)
        {
            return;
        }

        // No reading, no needle: a needle resting on the stop reads as a confident minimum.
        _needleShape.Visibility = Showing ? Visibility.Visible : Visibility.Hidden;

        if (!Showing)
        {
            return;
        }

        _needle.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = AngleFor(_reading.Value),
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ── Arc ───────────────────────────────────────────────────────────────────

    private void BuildArc()
    {
        var d = Math.Min(_spec.Width, _spec.Height);
        var cx = _spec.Width / 2;
        var cy = _spec.Height / 2;
        var t = _spec.Number("thickness", Math.Max(8, d * 0.08));
        var r = (d / 2) - (t / 2) - 2;

        var track = Arc(cx, cy, r, StartAngle, StartAngle + Sweep, t);
        Paint(track, Shape.StrokeProperty, _spec.Text("trackColour", ""), "@hairline");
        Children.Add(track);

        foreach (var zone in _spec.Zones.Where(z => StageLayout.IsColour(z.Colour) && z.To > z.From))
        {
            var band = Arc(cx, cy, r + (t / 2) + 4, AngleFor(zone.From), AngleFor(Math.Min(zone.To, _spec.Max)), 3);
            Paint(band, Shape.StrokeProperty, zone.Colour, zone.Colour);
            Children.Add(band);
        }

        if (_spec.MajorTick > 0)
        {
            for (var v = _spec.Min; v <= _spec.Max + 1e-6; v += _spec.MajorTick)
            {
                Children.Add(TickLine(cx, cy, r - (t / 2) - 3, 6, AngleFor(v), 2, _spec.Text("tickColour", ""), "@textLow"));
            }
        }

        AddCentreText(cx, cy, d);
    }

    private void DrawArcFill()
    {
        _dynamic.Children.Clear();

        if (!Showing || Fraction <= 0)
        {
            return;
        }

        var d = Math.Min(_spec.Width, _spec.Height);
        var t = _spec.Number("thickness", Math.Max(8, d * 0.08));
        var r = (d / 2) - (t / 2) - 2;
        var fill = Arc(_spec.Width / 2, _spec.Height / 2, r, StartAngle, StartAngle + Math.Max(0.5, Fraction * Sweep), t);
        Paint(fill, Shape.StrokeProperty, FillColour(), "@accent");
        _dynamic.Children.Add(fill);
    }

    // ── Bar ───────────────────────────────────────────────────────────────────

    private bool Vertical => _spec.Text("orientation", "horizontal").Equals("vertical", StringComparison.OrdinalIgnoreCase);

    private Rect BarTrack()
    {
        var t = _spec.Number("thickness", Vertical ? Math.Min(40, _spec.Width * 0.4) : Math.Min(24, _spec.Height * 0.35));
        return Vertical
            ? new Rect((_spec.Width - t) / 2, 34, t, Math.Max(10, _spec.Height - 34 - 34))
            : new Rect(0, _spec.Height - t - 6, _spec.Width, t);
    }

    private void BuildBar()
    {
        var track = BarTrack();
        var radius = _spec.Number("radius", Math.Min(track.Width, track.Height) / 2);

        var trackShape = new Rectangle { Width = track.Width, Height = track.Height, RadiusX = radius, RadiusY = radius };
        Paint(trackShape, Shape.FillProperty, _spec.Text("trackColour", ""), "@hairline");
        Place(trackShape, track.X, track.Y);
        Children.Add(trackShape);

        foreach (var zone in _spec.Zones.Where(z => StageLayout.IsColour(z.Colour) && z.To > z.From))
        {
            var (a, b) = (Frac(zone.From), Frac(Math.Min(zone.To, _spec.Max)));
            var strip = Vertical
                ? new Rectangle { Width = 4, Height = (b - a) * track.Height }
                : new Rectangle { Width = (b - a) * track.Width, Height = 4 };
            Paint(strip, Shape.FillProperty, zone.Colour, zone.Colour);
            if (Vertical)
            {
                Place(strip, track.Right + 4, track.Bottom - (b * track.Height));
            }
            else
            {
                Place(strip, track.X + (a * track.Width), track.Y - 7);
            }

            Children.Add(strip);
        }

        if (_spec.MajorTick > 0)
        {
            for (var v = _spec.Min; v <= _spec.Max + 1e-6; v += _spec.MajorTick)
            {
                var f = Frac(v);
                var tick = Vertical
                    ? new Line { X1 = track.X - 8, X2 = track.X - 2, Y1 = track.Bottom - (f * track.Height), Y2 = track.Bottom - (f * track.Height), StrokeThickness = 2 }
                    : new Line { X1 = track.X + (f * track.Width), X2 = track.X + (f * track.Width), Y1 = track.Y - 2, Y2 = track.Y - 8, StrokeThickness = 2 };
                Paint(tick, Shape.StrokeProperty, _spec.Text("tickColour", ""), "@textLow");
                Children.Add(tick);
            }
        }

        // Caption and readout: above the track when horizontal, above and below when vertical.
        if (_spec.Flag("showLabel", true) && _spec.Label.Length > 0)
        {
            var caption = Label(_spec.Label, _spec.Number("labelSize", 14), "mono", _spec.Text("labelColour", ""), "@caption", FontWeights.SemiBold);
            if (Vertical)
            {
                caption.Width = _spec.Width;
                caption.TextAlignment = TextAlignment.Center;
                Place(caption, 0, _spec.Height - 26);
            }
            else
            {
                Place(caption, 0, 0);
            }
        }

        if (_spec.Flag("showValue", true))
        {
            _value = Label("", _spec.Number("valueSize", Vertical ? 18 : 22), "ui", _spec.Text("valueColour", ""), "@textHigh", FontWeights.Bold);
            _value.Width = _spec.Width;
            _value.TextAlignment = Vertical ? TextAlignment.Center : TextAlignment.Right;
            Place(_value, 0, Vertical ? 0 : -4);
        }
    }

    private void DrawBarFill()
    {
        _dynamic.Children.Clear();

        if (!Showing || Fraction <= 0)
        {
            return;
        }

        var track = BarTrack();
        var radius = _spec.Number("radius", Math.Min(track.Width, track.Height) / 2);
        var fill = Vertical
            ? new Rectangle { Width = track.Width, Height = Math.Max(track.Width, Fraction * track.Height), RadiusX = radius, RadiusY = radius }
            : new Rectangle { Width = Math.Max(track.Height, Fraction * track.Width), Height = track.Height, RadiusX = radius, RadiusY = radius };
        Paint(fill, Shape.FillProperty, FillColour(), "@accent");
        Place(fill, track.X, Vertical ? track.Bottom - fill.Height : track.Y);
        _dynamic.Children.Add(fill);
    }

    // ── LCARS bar ─────────────────────────────────────────────────────────────

    private int Segments => (int)Math.Clamp(_spec.Number("segments", 20), 2, 80);

    private (Rect Cap, Rect Run) LcarsGeometry()
    {
        var cap = _spec.Number("capWidth", Vertical ? _spec.Width : Math.Min(150, _spec.Width * 0.3));
        var gap = _spec.Number("segmentGap", 6);

        return Vertical
            ? (new Rect(0, _spec.Height - Math.Min(90, _spec.Height * 0.3), _spec.Width, Math.Min(90, _spec.Height * 0.3)),
               new Rect(0, 0, _spec.Width, _spec.Height - Math.Min(90, _spec.Height * 0.3) - gap))
            : (new Rect(0, 0, cap, _spec.Height), new Rect(cap + gap, 0, Math.Max(10, _spec.Width - cap - gap), _spec.Height));
    }

    private void BuildLcars()
    {
        var (cap, _) = LcarsGeometry();

        // The cap: a pill-ended block carrying the caption and the number, black on colour.
        var r = Math.Min(cap.Width, cap.Height) / 2;
        var capShape = new Border
        {
            Width = cap.Width,
            Height = cap.Height,
            CornerRadius = Vertical ? new CornerRadius(0, 0, r, r) : new CornerRadius(r, 0, 0, r),
        };
        Paint(capShape, Border.BackgroundProperty, _spec.Text("capColour", ""), "@caption");
        Place(capShape, cap.X, cap.Y);
        Children.Add(capShape);

        var stack = new StackPanel { Width = cap.Width, VerticalAlignment = VerticalAlignment.Center };

        if (_spec.Flag("showLabel", true) && _spec.Label.Length > 0)
        {
            var caption = Label(_spec.Label, _spec.Number("labelSize", 15), "mono", _spec.Text("labelColour", "#000000"), "#000000", FontWeights.Bold);
            caption.TextAlignment = TextAlignment.Right;
            caption.Margin = new Thickness(0, 0, 14, 0);
            Children.Remove(caption);
            stack.Children.Add(caption);
        }

        if (_spec.Flag("showValue", true))
        {
            _value = Label("", _spec.Number("valueSize", 26), "ui", _spec.Text("valueColour", "#000000"), "#000000", FontWeights.Bold);
            _value.TextAlignment = TextAlignment.Right;
            _value.Margin = new Thickness(0, 0, 14, 0);
            Children.Remove(_value);
            stack.Children.Add(_value);
        }

        stack.Measure(new Size(cap.Width, cap.Height));
        Place(stack, cap.X, cap.Y + Math.Max(0, (cap.Height - stack.DesiredSize.Height) / 2));
        Children.Add(stack);

        DrawLcarsSegments(this, lit: 0, track: true);
    }

    private void DrawLcarsFill()
    {
        _dynamic.Children.Clear();
        DrawLcarsSegments(_dynamic, Showing ? (int)Math.Round(Fraction * Segments) : 0, track: false);
    }

    /// <summary>
    /// The segments: unlit ones on the static face, lit ones in the dynamic layer. A lit segment
    /// inside a zone takes the zone's colour, so the redline lights red.
    /// </summary>
    private void DrawLcarsSegments(Panel layer, int lit, bool track)
    {
        var (_, run) = LcarsGeometry();
        var gap = _spec.Number("segmentGap", 6);
        var n = Segments;
        var thickness = _spec.Number("thickness", Vertical ? run.Width : run.Height);
        var size = ((Vertical ? run.Height : run.Width) - (gap * (n - 1))) / n;

        for (var i = 0; i < n; i++)
        {
            var on = !track && i < lit;
            if (!track && !on)
            {
                break;
            }

            var pill = Vertical
                ? new Rectangle { Width = thickness, Height = Math.Max(2, size) }
                : new Rectangle { Width = Math.Max(2, size), Height = thickness };
            pill.RadiusX = pill.RadiusY = Math.Min(pill.Width, pill.Height) / 2;

            var valueAt = _spec.Min + ((i + 0.5) / n * (_spec.Max - _spec.Min));
            var zone = _spec.Zones.FirstOrDefault(z => valueAt >= z.From && valueAt <= z.To && StageLayout.IsColour(z.Colour));
            Paint(pill, Shape.FillProperty, on ? zone?.Colour ?? FillColourAt(valueAt) : _spec.Text("trackColour", ""), on ? "@accent" : "@hairline");

            var offset = i * (size + gap);
            if (Vertical)
            {
                Place(pill, run.X + ((run.Width - thickness) / 2), run.Bottom - offset - size);
            }
            else
            {
                Place(pill, run.X + offset, run.Y + ((run.Height - thickness) / 2));
            }

            layer.Children.Add(pill);
        }
    }

    // ── Digital ───────────────────────────────────────────────────────────────

    private void BuildDigital()
    {
        var frame = _spec.Text("frameColour", "none");
        if (!frame.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var border = new Border
            {
                Width = _spec.Width,
                Height = _spec.Height,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(_spec.Number("frameRadius", 14)),
            };
            Paint(border, Border.BorderBrushProperty, frame, "@hairlineStrong");
            Children.Add(border);
        }

        AddCentreText(_spec.Width / 2, _spec.Height / 2, Math.Min(_spec.Width, _spec.Height) * 1.4);
    }

    /// <summary>Caption above, number below, centred on a point — the arc's and the digital gauge's middle.</summary>
    private void AddCentreText(double cx, double cy, double d)
    {
        if (_spec.Flag("showValue", true))
        {
            _value = Label("", _spec.Number("valueSize", Math.Max(16, d * 0.16)), "ui", _spec.Text("valueColour", ""), "@textHigh", FontWeights.Bold);
            _value.Width = _spec.Width;
            _value.TextAlignment = TextAlignment.Center;
            _value.Measure(new Size(_spec.Width, double.PositiveInfinity));
            Place(_value, 0, cy - (_value.DesiredSize.Height / 2));
        }

        if (_spec.Flag("showLabel", true) && _spec.Label.Length > 0)
        {
            var caption = Label(_spec.Label, _spec.Number("labelSize", Math.Max(10, d * 0.06)), "mono", _spec.Text("labelColour", ""), "@caption", FontWeights.SemiBold);
            Centre(caption, new Point(cx, cy + Math.Max(18, d * 0.15)));
        }
    }

    // ── Colour ────────────────────────────────────────────────────────────────

    /// <summary>The fill: the colour of the zone the value is in, else fillColour, else the accent.</summary>
    private string FillColour() => FillColourAt(_reading.Value);

    private string FillColourAt(double value) =>
        _spec.Zones.FirstOrDefault(z => value >= z.From && value <= z.To && StageLayout.IsColour(z.Colour))?.Colour
        ?? _spec.Text("fillColour", "@accent");

    /// <summary>
    /// Set a brush property from a layout colour. <c>@token</c> becomes a live theme reference —
    /// it changes with the theme and dims at night — and <c>#RRGGBB</c> a fixed brush.
    /// </summary>
    private static void Paint(DependencyObject target, DependencyProperty property, string? colour, string fallback)
    {
        foreach (var candidate in new[] { colour, fallback })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.StartsWith('@') && ThemeTokens.TryGet(candidate[1..], out var token) && token.Kind is TokenKind.Colour)
            {
                if (target is FrameworkElement element)
                {
                    element.SetResourceReference(property, token.ResourceKey);
                }
                else
                {
                    target.SetValue(property, Application.Current?.TryFindResource(token.ResourceKey));
                }

                return;
            }

            if (ThemeColour.TryParse(candidate, out var c))
            {
                target.SetValue(property, Frozen(c));
                return;
            }
        }
    }

    /// <summary>A layout colour as a brush now, for the needle's glow, which is not a brush property.</summary>
    private static Brush? Resolve(string colour) =>
        colour.StartsWith('@') && ThemeTokens.TryGet(colour[1..], out var token)
            ? Application.Current?.TryFindResource(token.ResourceKey) as Brush
            : ThemeColour.TryParse(colour, out var c) ? Frozen(c) : null;

    // ── Drawing helpers ───────────────────────────────────────────────────────

    private double Frac(double v) => Math.Clamp((v - _spec.Min) / (_spec.Max - _spec.Min), 0, 1);

    private TextBlock Label(string text, double size, string font, string colour, string fallback, FontWeight weight)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
        t.SetResourceReference(TextBlock.FontFamilyProperty, font == "ui" ? "UiFont" : "MonoFont");
        Paint(t, TextBlock.ForegroundProperty, colour, fallback);
        Children.Add(t);
        return t;
    }

    private static void Centre(FrameworkElement element, Point centre)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(element, centre.X - (element.DesiredSize.Width / 2), centre.Y - (element.DesiredSize.Height / 2));
    }

    private static void Place(UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
    }

    private static Point OnCircle(double cx, double cy, double r, double angleDeg)
    {
        var a = angleDeg * Math.PI / 180.0;
        return new Point(cx + (r * Math.Sin(a)), cy - (r * Math.Cos(a)));
    }

    private static bool IsMultiple(double v, double step)
    {
        var n = v / step;
        return Math.Abs(n - Math.Round(n)) < 1e-6;
    }

    private static Ellipse Disc(double x, double y, double size, Brush? fill, Brush? stroke, double thickness)
    {
        var e = new Ellipse { Width = Math.Max(0, size), Height = Math.Max(0, size), Fill = fill, Stroke = stroke, StrokeThickness = thickness };
        Place(e, x, y);
        return e;
    }

    private static Path Arc(double cx, double cy, double r, double startDeg, double endDeg, double thickness)
    {
        var start = OnCircle(cx, cy, r, startDeg);
        var end = OnCircle(cx, cy, r, endDeg);
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, Math.Abs(endDeg - startDeg) > 180, SweepDirection.Clockwise, true));
        return new Path
        {
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry([figure]),
        };
    }

    private static SolidColorBrush Frozen(string hex) =>
        ThemeColour.TryParse(hex, out var c) ? Frozen(c) : Brushes.Transparent;

    private static SolidColorBrush Frozen(ThemeColour c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(params (string Hex, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        foreach (var (hex, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex), offset));
        }

        brush.Freeze();
        return brush;
    }
}
