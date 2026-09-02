using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DashDeck.Host.Stage;

/// <summary>
/// The compass rose. Bound to <see cref="ViewModels.CompassViewModel"/>.
/// </summary>
/// <remarks>
/// The ticks and the letters are built here rather than in XAML. Seventy-two tick marks
/// written out by hand would be unreadable markup and unmaintainable geometry, and the
/// positions are trigonometry — which belongs in a loop, not in seventy-two sets of
/// coordinates that nobody will ever dare change.
/// <para>
/// Drawing rather than laying out, and it is the first thing on the dash that is. F5 expects
/// this to become common as gauges arrive; the theme brushes are still looked up by key, so
/// a drawn surface changes colour with the rest of the dash rather than becoming the one
/// thing that stays orange at night.
/// </para>
/// </remarks>
public partial class CompassView : UserControl
{
    private const double Size = 380;
    private const double Centre = Size / 2;

    public CompassView()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildRose();
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

            var length = isCardinal ? 22 : isMajor ? 16 : 8;
            var outer = Centre - 14;
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

            tick.X1 = Centre + (Math.Cos(radians) * inner);
            tick.Y1 = Centre + (Math.Sin(radians) * inner);
            tick.X2 = Centre + (Math.Cos(radians) * outer);
            tick.Y2 = Centre + (Math.Sin(radians) * outer);

            Rose.Children.Add(tick);
        }

        string[] letters = ["N", "E", "S", "W"];

        for (var i = 0; i < letters.Length; i++)
        {
            var label = new TextBlock
            {
                Text = letters[i],
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = i == 0 ? 26 : 20,
                Foreground = Brush(i == 0 ? "AccentBrush" : "TextMidBrush"),
            };

            // Measured before placing, because a letter is centred on its point and its
            // width is not known until it has been asked.
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var radians = ((i * 90) - 90) * Math.PI / 180;
            var radius = Centre - 62;

            Canvas.SetLeft(label, Centre + (Math.Cos(radians) * radius) - (label.DesiredSize.Width / 2));
            Canvas.SetTop(label, Centre + (Math.Sin(radians) * radius) - (label.DesiredSize.Height / 2));

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
    private Brush Brush(string key) =>
        FindResource(key) as Brush ?? Brushes.Gray;
}
