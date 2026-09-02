using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// The compass rose and the G meter. Bound to <see cref="CompassViewModel"/>.
/// </summary>
/// <remarks>
/// Both are built here rather than in XAML. Seventy-two tick marks and a set of concentric
/// g rings written out by hand would be unreadable markup and unmaintainable geometry, and
/// the positions are trigonometry — which belongs in a loop, not in seventy-two sets of
/// coordinates nobody will ever dare change.
/// <para>
/// Drawing rather than laying out, and the first place on the dash that does. F5 expects this
/// to become common as gauges arrive; the theme brushes are still looked up by key, so a
/// drawn surface changes colour with the rest of the dash rather than becoming the one thing
/// that stays orange at night.
/// </para>
/// </remarks>
public partial class CompassView : UserControl
{
    private const double RoseSize = 320;
    private const double RoseCentre = RoseSize / 2;

    private const double GridSize = 240;
    private const double GridCentre = GridSize / 2;

    /// <summary>
    /// How far one g moves the ball.
    /// </summary>
    /// <remarks>
    /// The outer ring is 1 g, which a pickup will never reach — but a scale sized to what a
    /// truck actually does would put the ball off the edge the one time something goes badly
    /// wrong, and that is the reading that matters most.
    /// </remarks>
    private const double PixelsPerG = GridCentre - 18;

    private CompassViewModel? _model;

    public CompassView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            BuildRose();
            BuildGrid();
            Attach();
            MoveBall();
        };

        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();

        _model = DataContext as CompassViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model = null;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompassViewModel.LateralG)
            or nameof(CompassViewModel.LongitudinalG))
        {
            MoveBall();
        }
    }

    /// <summary>
    /// Put the ball where the force is.
    /// </summary>
    /// <remarks>
    /// <b>The ball moves the way the driver is pushed</b>, not the way the truck accelerates —
    /// so braking throws it forward, towards the top of the display, and a right-hand bend
    /// throws it left. That is the convention every G meter in a car uses and the one that
    /// matches what the body is already feeling; the opposite convention is equally defensible
    /// on paper and reads as broken in a moving vehicle.
    /// </remarks>
    private void MoveBall()
    {
        if (_model is null)
        {
            return;
        }

        BallShift.X = Math.Clamp(-_model.LateralG * PixelsPerG, -PixelsPerG, PixelsPerG);
        BallShift.Y = Math.Clamp(_model.LongitudinalG * PixelsPerG, -PixelsPerG, PixelsPerG);
    }

    private void BuildGrid()
    {
        if (GGrid.Children.Count > 0)
        {
            return;
        }

        // Rings at a quarter, a half and a whole g. Three is enough to read a position
        // against at a glance; more would be a target, not a gauge.
        foreach (var g in new[] { 0.25, 0.5, 1.0 })
        {
            var radius = g * PixelsPerG;

            var ring = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = Brush(g >= 1.0 ? "HairlineStrongBrush" : "HairlineBrush"),
                StrokeThickness = 1,
            };

            Canvas.SetLeft(ring, GridCentre - radius);
            Canvas.SetTop(ring, GridCentre - radius);
            GGrid.Children.Add(ring);
        }

        // Crosshair, stopping short of the centre so it never competes with the ball.
        foreach (var (x1, y1, x2, y2) in new[]
        {
            (GridCentre - PixelsPerG, GridCentre, GridCentre - 14, GridCentre),
            (GridCentre + 14, GridCentre, GridCentre + PixelsPerG, GridCentre),
            (GridCentre, GridCentre - PixelsPerG, GridCentre, GridCentre - 14),
            (GridCentre, GridCentre + 14, GridCentre, GridCentre + PixelsPerG),
        })
        {
            GGrid.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = Brush("HairlineBrush"),
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
            });
        }
    }

    private void BuildRose()
    {
        if (Rose.Children.Count > 0)
        {
            return;
        }

        for (var degrees = 0; degrees < 360; degrees += 5)
        {
            var isCardinal = degrees % 90 == 0;
            var isMajor = degrees % 45 == 0;

            var length = isCardinal ? 20 : isMajor ? 14 : 7;
            var outer = RoseCentre - 12;
            var inner = outer - length;

            var tick = new Line
            {
                StrokeThickness = isMajor ? 2 : 1,
                Stroke = Brush(isCardinal ? "AccentBrush" : isMajor ? "TextLowBrush" : "TextFaintBrush"),
                SnapsToDevicePixels = true,
            };

            // Screen angles run clockwise from twelve o'clock; the maths runs anticlockwise
            // from three. Hence the -90.
            var radians = (degrees - 90) * Math.PI / 180;

            tick.X1 = RoseCentre + (Math.Cos(radians) * inner);
            tick.Y1 = RoseCentre + (Math.Sin(radians) * inner);
            tick.X2 = RoseCentre + (Math.Cos(radians) * outer);
            tick.Y2 = RoseCentre + (Math.Sin(radians) * outer);

            Rose.Children.Add(tick);
        }

        string[] letters = ["N", "E", "S", "W"];

        for (var i = 0; i < letters.Length; i++)
        {
            var label = new TextBlock
            {
                Text = letters[i],
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = i == 0 ? 24 : 18,
                Foreground = Brush(i == 0 ? "AccentBrush" : "TextMidBrush"),
            };

            // Measured before placing, because a letter is centred on its point and its width
            // is not known until it has been asked.
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var radians = ((i * 90) - 90) * Math.PI / 180;
            var radius = RoseCentre - 54;

            Canvas.SetLeft(label, RoseCentre + (Math.Cos(radians) * radius) - (label.DesiredSize.Width / 2));
            Canvas.SetTop(label, RoseCentre + (Math.Sin(radians) * radius) - (label.DesiredSize.Height / 2));

            Rose.Children.Add(label);
        }
    }

    /// <summary>
    /// A theme brush by key.
    /// </summary>
    /// <remarks>
    /// Looked up rather than hard-coded, so a drawn surface follows day and night with
    /// everything else. It is a live lookup at build time only — the rose is rebuilt when the
    /// view is, which is every time the occupant is chosen, so a theme change mid-session
    /// leaves the ticks as they were until then. Acceptable for ticks; it would not be for a
    /// value.
    /// </remarks>
    private Brush Brush(string key) => FindResource(key) as Brush ?? Brushes.Gray;
}
