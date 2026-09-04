using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace Tpms;

/// <summary>
/// Per-wheel tyre pressure, with an overhead view of the truck.
/// </summary>
/// <remarks>
/// The widget is a glance — the lowest tyre, and whether anything is low. The detail is the
/// point: a top-down 2019 F-150 with the pressure at each corner and the low one lit, which is
/// a far better way to answer "which tyre?" than a list of four numbers.
/// <para>
/// TPMS is not standard OBD-II — Ford carries it on an MS-CAN body-module message whose real
/// PID is undiscovered (see the catalog), so on the synthetic truck these read Simulated and on
/// a real truck without the PID they read Unavailable and every corner shows a dash. The
/// component neither knows nor cares which: it subscribes to four named signals and renders
/// their quality honestly (rule 3).
/// </para>
/// </remarks>
public sealed class Component : IDashComponent, IDashComponentView
{
    /// <summary>Below this, a tyre is called low and lit. Placard is about 35 psi.</summary>
    private const double LowPsi = 30.0;

    // Corner order used everywhere: front-left, front-right, rear-left, rear-right.
    private static readonly string[] SignalIds =
    [
        "tire.frontLeft.pressure",
        "tire.frontRight.pressure",
        "tire.rearLeft.pressure",
        "tire.rearRight.pressure",
    ];

    private static readonly string[] CornerNames = ["FRONT LEFT", "FRONT RIGHT", "REAR LEFT", "REAR RIGHT"];

    private IComponentContext _context = null!;
    private readonly ISignalSubscription?[] _demands = new ISignalSubscription?[4];
    private readonly IDisposable?[] _subs = new IDisposable?[4];
    private readonly SignalValue[] _tires = new SignalValue[4];

    /// <inheritdoc />
    public string Id => "com.jpenner.tpms";

    /// <inheritdoc />
    public Task InitializeAsync(IComponentContext context, CancellationToken ct)
    {
        _context = context;
        for (var i = 0; i < 4; i++)
        {
            _tires[i] = SignalValue.Missing(SignalIds[i]);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            _demands[i] = _context.Signals.Require(SignalIds[i], SignalPriority.Low, 0.2);
            _subs[i] = _context.Signals.Subscribe(SignalIds[i], v => _tires[index] = v);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        for (var i = 0; i < 4; i++)
        {
            _subs[i]?.Dispose();
            _demands[i]?.Dispose();
            _subs[i] = null;
            _demands[i] = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SuspendAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken ct) => StartAsync(ct);

    /// <summary>The lowest usable tyre pressure, or NaN if none is usable.</summary>
    private double LowestUsable()
    {
        var lowest = double.NaN;
        foreach (var t in _tires)
        {
            if (t.IsUsable && (double.IsNaN(lowest) || t.Value < lowest))
            {
                lowest = t.Value;
            }
        }

        return lowest;
    }

    /// <inheritdoc />
    public FrameworkElement CreateWidget()
    {
        var caption = new TextBlock
        {
            Text = "TIRES",
            FontFamily = Font("MonoFont"),
            FontSize = 12,
            Foreground = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E)),
        };

        var value = new TextBlock
        {
            FontFamily = Font("UiFont"),
            FontSize = 50,
            FontWeight = FontWeights.Bold,
        };

        var unit = new TextBlock
        {
            Text = "psi",
            FontFamily = Font("MonoFont"),
            FontSize = 17,
            Foreground = Brush("TextMidBrush", Color.FromRgb(0xB8, 0xB3, 0xA8)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 9),
        };

        var number = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        number.Children.Add(value);
        number.Children.Add(unit);
        Grid.SetRow(number, 1);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(caption);
        grid.Children.Add(number);

        var high = Brush("TextHighBrush", Color.FromRgb(0xF2, 0xEF, 0xE9));
        var amber = new SolidColorBrush(Color.FromRgb(0xE0, 0xB2, 0x3C));

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            var lowest = LowestUsable();

            if (double.IsNaN(lowest))
            {
                value.Text = "—";
                value.Foreground = high;
                unit.Visibility = Visibility.Collapsed;
                return;
            }

            value.Text = lowest.ToString("0", CultureInfo.CurrentCulture);
            value.Foreground = lowest < LowPsi ? amber : high;   // draw the eye when a tyre is low
            unit.Visibility = Visibility.Visible;
        };
        timer.Start();
        grid.Unloaded += (_, _) => timer.Stop();

        return grid;
    }

    /// <inheritdoc />
    public FrameworkElement CreateFullScreen()
    {
        // A design-sized canvas in a Viewbox, so the truck scales to whatever space the detail
        // has without any pixel arithmetic here.
        const double W = 700, H = 1040;
        var canvas = new Canvas { Width = W, Height = H };

        var white = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xE8));      // the truck is white
        var panel = new SolidColorBrush(Color.FromRgb(0xD7, 0xD7, 0xD2));
        var glass = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1A));
        var bed = new SolidColorBrush(Color.FromRgb(0x20, 0x1F, 0x1A));
        var edge = new SolidColorBrush(Color.FromRgb(0xB6, 0xB6, 0xB0));
        var high = Brush("TextHighBrush", Color.FromRgb(0xF2, 0xEF, 0xE9));
        var mid = Brush("TextMidBrush", Color.FromRgb(0xB8, 0xB3, 0xA8));
        var low = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E));
        var amber = new SolidColorBrush(Color.FromRgb(0xE0, 0xB2, 0x3C));
        var wheelDark = new SolidColorBrush(Color.FromRgb(0x2C, 0x2B, 0x28));

        // Body: one rounded rectangle, nose up. Clean rather than photoreal — it matches the
        // rest of the design system, and a stylised truck reads faster than a detailed one.
        canvas.Children.Add(Box(210, 90, 280, 860, 52, white, edge, 2));
        // Hood seam and grille at the nose.
        canvas.Children.Add(Box(250, 104, 200, 22, 6, glass, null, 0));
        // Cab glass (windshield + roof), toward the front third.
        canvas.Children.Add(Box(232, 300, 236, 150, 18, glass, null, 0));
        // Bed, with a few ribs and a tailgate line.
        canvas.Children.Add(Box(230, 520, 240, 380, 12, bed, edge, 1));
        for (var r = 1; r <= 3; r++)
        {
            canvas.Children.Add(Rib(240, 520 + r * 95, 220, panel));
        }

        // Wheels straddle the body edges, front and rear.
        var wheels = new Rectangle[4];
        double[,] wheelAt = { { 181, 250 }, { 485, 250 }, { 181, 700 }, { 485, 700 } };
        for (var i = 0; i < 4; i++)
        {
            wheels[i] = (Rectangle)Box(wheelAt[i, 0], wheelAt[i, 1], 34, 96, 12, wheelDark, null, 0);
            canvas.Children.Add(wheels[i]);
        }

        // Four readouts at the outer corners, each aligned toward its wheel.
        var values = new TextBlock[4];
        double[,] readoutAt = { { 20, 250 }, { 530, 250 }, { 20, 700 }, { 530, 700 } };
        for (var i = 0; i < 4; i++)
        {
            var alignRight = i is 0 or 2;   // left-of-screen corners point right, toward the truck
            var readout = Readout(CornerNames[i], alignRight, low, high, mid, out values[i]);
            Canvas.SetLeft(readout, readoutAt[i, 0]);
            Canvas.SetTop(readout, readoutAt[i, 1]);
            canvas.Children.Add(readout);
        }

        // A one-line verdict at the top, so the answer is there before you read the corners.
        var status = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Width = W,
            TextAlignment = TextAlignment.Center,
        };
        Canvas.SetTop(status, 24);
        canvas.Children.Add(status);

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            var lowestIndex = -1;
            var lowestValue = double.NaN;

            for (var i = 0; i < 4; i++)
            {
                var t = _tires[i];

                if (!t.IsUsable)
                {
                    values[i].Text = "—";
                    values[i].Foreground = low;
                    wheels[i].Fill = wheelDark;
                    continue;
                }

                var isLow = t.Value < LowPsi;
                values[i].Text = t.Value.ToString("0", CultureInfo.CurrentCulture);
                values[i].Foreground = isLow ? amber : high;
                wheels[i].Fill = isLow ? amber : wheelDark;

                if (double.IsNaN(lowestValue) || t.Value < lowestValue)
                {
                    lowestValue = t.Value;
                    lowestIndex = i;
                }
            }

            if (lowestIndex < 0)
            {
                status.Text = "NO TYRE DATA";
                status.Foreground = low;
            }
            else if (lowestValue < LowPsi)
            {
                status.Text = $"{CornerNames[lowestIndex]} LOW";
                status.Foreground = amber;
            }
            else
            {
                status.Text = "ALL TYRES OK";
                status.Foreground = mid;
            }
        };
        timer.Start();
        canvas.Unloaded += (_, _) => timer.Stop();

        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 24),
            Child = canvas,
        };
    }

    /// <summary>A rounded rectangle placed on the canvas.</summary>
    private static Rectangle Box(double x, double y, double w, double h, double radius, Brush fill, Brush? stroke, double thickness)
    {
        var r = new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    /// <summary>A faint horizontal line, for bed ribs.</summary>
    private static Line Rib(double x, double y, double w, Brush brush) => new()
    {
        X1 = x,
        Y1 = y,
        X2 = x + w,
        Y2 = y,
        Stroke = brush,
        StrokeThickness = 2,
        Opacity = 0.5,
    };

    private StackPanel Readout(string label, bool alignRight, Brush low, Brush high, Brush mid, out TextBlock value)
    {
        var align = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var caption = new TextBlock
        {
            Text = label,
            FontFamily = Font("MonoFont"),
            FontSize = 13,
            Foreground = low,
            HorizontalAlignment = align,
            Margin = new Thickness(0, 0, 0, 2),
        };

        value = new TextBlock
        {
            Text = "—",
            FontFamily = Font("UiFont"),
            FontSize = 46,
            FontWeight = FontWeights.Bold,
            Foreground = high,
        };

        var unit = new TextBlock
        {
            Text = "psi",
            FontFamily = Font("MonoFont"),
            FontSize = 15,
            Foreground = mid,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 8),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align };
        row.Children.Add(value);
        row.Children.Add(unit);

        var stack = new StackPanel { Width = 150 };
        stack.Children.Add(caption);
        stack.Children.Add(row);
        return stack;
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");
}
