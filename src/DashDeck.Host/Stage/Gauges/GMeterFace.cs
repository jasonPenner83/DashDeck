using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DashDeck.Abstractions;
using DashDeck.Host.Converters;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// A G meter on the stage (ADR-0039): rings, a crosshair, a ball pushed the way the driver is,
/// and the total and peak g underneath.
/// </summary>
/// <remarks>
/// The old COMPASS screen's G meter, drawn from a layout element. Its outer ring is <c>range</c>
/// g — 1 by default, which a pickup will never reach, so the one time something goes badly wrong
/// the ball is still on the meter. <b>Not levelled, no ball:</b> lateral and longitudinal g are
/// measured against the mount, so until it is levelled the meter says so instead of parking the
/// ball at a number that is about the cradle, not the truck.
/// </remarks>
public sealed class GMeterFace : Canvas
{
    private readonly GaugeSpec _spec;
    private readonly double _size;
    private readonly double _radius;
    private readonly double _range;
    private readonly Ellipse _ball;
    private readonly TranslateTransform _ballShift = new();
    private readonly TextBlock _notLevelled;
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Fill = QualityPalette.Unavailable };
    private TextBlock? _g;
    private TextBlock? _peak;
    private TextBlock? _source;

    public GMeterFace(GaugeSpec spec)
    {
        _spec = spec;
        Width = spec.Width;
        Height = spec.Height;

        var showValue = spec.Flag("showValue", true);
        var showSource = spec.Flag("showSource", true);
        _size = Math.Max(40, Math.Min(spec.Width, spec.Height - (showValue ? 44 : 0) - (showSource ? 18 : 0)));
        _range = Math.Clamp(spec.Number("range", 1), 0.05, 10);
        _radius = (_size / 2) - (18 * _size / 240);

        var ballSize = spec.Number("ballSize", 26 * _size / 240);
        _ball = new Ellipse { Width = ballSize, Height = ballSize, RenderTransform = _ballShift };
        _notLevelled = new TextBlock
        {
            Width = _size * 0.75,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Text = "Park level, then LEVEL in Settings ▸ Mount — the mount angle is not the truck's",
        };

        Build(showValue, showSource);
    }

    /// <summary>
    /// Draw a reading: lateral and longitudinal g (smoothed; NaN when there is none), the peak so
    /// far, how far to trust it, where it came from, and whether the mount is levelled.
    /// </summary>
    public void Show(double lateral, double longitudinal, double peak, SignalQuality quality, string source, bool levelled)
    {
        var has = levelled && !double.IsNaN(lateral) && !double.IsNaN(longitudinal);

        _ball.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _notLevelled.Visibility = levelled ? Visibility.Collapsed : Visibility.Visible;

        if (has)
        {
            var (x, y) = SensorMath.Ball(lateral, longitudinal, _range, _radius);
            _ballShift.X = x;
            _ballShift.Y = y;

            if (string.IsNullOrEmpty(_spec.Text("ballColour", "")))
            {
                _ball.Fill = QualityPalette.For(quality);
            }
        }

        if (_g is not null)
        {
            _g.Text = has ? SensorMath.Total(lateral, longitudinal).ToString("0.00", CultureInfo.CurrentCulture) : "—.——";
        }

        if (_peak is not null)
        {
            _peak.Text = double.IsNaN(peak) ? "—.——" : peak.ToString("0.00", CultureInfo.CurrentCulture);
        }

        if (_source is not null)
        {
            _source.Text = source;
            _source.Foreground = QualityPalette.For(has ? quality : SignalQuality.Unavailable);
        }

        _dot.Fill = QualityPalette.For(has ? quality : SignalQuality.Unavailable);
    }

    private void Build(bool showValue, bool showSource)
    {
        var left = (_spec.Width - _size) / 2;
        var c = _size / 2;
        var grid = new Canvas { Width = _size, Height = _size };

        // Rings: a quarter, a half and the whole range by default — enough to read a position
        // against at a glance; more would be a target, not a gauge.
        var count = (int)Math.Clamp(_spec.Number("rings", 3), 1, 8);
        double[] fractions = count == 3 ? [0.25, 0.5, 1.0] : [.. Enumerable.Range(1, count).Select(i => (double)i / count)];

        foreach (var fraction in fractions)
        {
            var r = fraction * _radius;
            var ring = new Ellipse { Width = r * 2, Height = r * 2, StrokeThickness = 1 };
            if (fraction >= 1)
            {
                GaugeFace.Paint(ring, Shape.StrokeProperty, _spec.Text("outerRingColour", ""), "@hairlineStrong");
            }
            else
            {
                GaugeFace.Paint(ring, Shape.StrokeProperty, _spec.Text("ringColour", ""), "@hairline");
            }

            Place(ring, c - r, c - r);
            grid.Children.Add(ring);
        }

        // A crosshair that stops short of the centre, so it never competes with the ball.
        var cross = _spec.Text("crossColour", "@hairline");
        if (!cross.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var gap = 14 * _size / 240;
            foreach (var (x1, y1, x2, y2) in new[]
            {
                (c - _radius, c, c - gap, c),
                (c + gap, c, c + _radius, c),
                (c, c - _radius, c, c - gap),
                (c, c + gap, c, c + _radius),
            })
            {
                var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, SnapsToDevicePixels = true };
                GaugeFace.Paint(line, Shape.StrokeProperty, cross, "@hairline");
                grid.Children.Add(line);
            }
        }

        if (_spec.Text("ballColour", "") is { Length: > 0 } ballColour)
        {
            GaugeFace.Paint(_ball, Shape.FillProperty, ballColour, "@accent");
        }

        Place(_ball, c - (_ball.Width / 2), c - (_ball.Height / 2));
        grid.Children.Add(_ball);

        _notLevelled.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _notLevelled.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
        _notLevelled.Measure(new Size(_notLevelled.Width, double.PositiveInfinity));
        Place(_notLevelled, c - (_notLevelled.Width / 2), c - (_notLevelled.DesiredSize.Height / 2));
        grid.Children.Add(_notLevelled);

        Place(grid, left, 0);
        Children.Add(grid);

        var y = _size + 4;
        if (showValue)
        {
            var size = _spec.Number("valueSize", 26);
            _g = Readout("G ", left, y, size, HorizontalAlignment.Left, "@textHigh");
            _peak = Readout("PEAK ", left, y, size, HorizontalAlignment.Right, "@textMid");
            y += size + 14;
        }

        if (showSource)
        {
            _source = new TextBlock { FontSize = 11, Width = _spec.Width, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _source.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            Place(_source, 0, Math.Min(y, _spec.Height - 15));
            Children.Add(_source);
        }

        Place(_dot, _spec.Width - 13, 4);
        Children.Add(_dot);

        Show(double.NaN, double.NaN, double.NaN, SignalQuality.Unavailable, "", levelled: true);
    }

    /// <summary>A caption and a number on one line, at one side of the meter.</summary>
    private TextBlock Readout(string caption, double left, double y, double size, HorizontalAlignment side, string valueFallback)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Width = _size, HorizontalAlignment = side };
        var label = new TextBlock { Text = caption, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        GaugeFace.Paint(label, TextBlock.ForegroundProperty, _spec.Text("labelColour", ""), "@caption");

        var value = new TextBlock { FontSize = size, FontWeight = FontWeights.Bold };
        value.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        GaugeFace.Paint(value, TextBlock.ForegroundProperty, side == HorizontalAlignment.Left ? _spec.Text("valueColour", "") : "", valueFallback);

        row.Children.Add(label);
        row.Children.Add(value);

        var holder = new Grid { Width = _size };
        row.Width = double.NaN;
        holder.Children.Add(row);
        Place(holder, left, y);
        Children.Add(holder);
        return value;
    }

    private static void Place(UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
    }
}
