using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DashDeck.Host.Stage;

/// <summary>
/// A strip beside a hosted program that scrolls it: drag a finger along it and the program
/// scrolls as if a wheel were turned (ADR-0046).
/// </summary>
/// <remarks>
/// <b>Manipulation, not touch or mouse events</b> — the trap in CLAUDE.md: on glass a touch is
/// consumed before it would become a mouse event, so the strip reads the manipulation itself, and
/// a flick carries on by inertia the way a list does under a finger. A mouse drag and the mouse's
/// own wheel are honoured too, so it can be tried at a desk; they are not how it is used.
/// <para>
/// It raises <see cref="Scrolled"/> with a whole-notch wheel delta and the finger's height on
/// the strip, and knows nothing of who receives it.
/// </para>
/// </remarks>
public sealed class ScrollStripView : Border
{
    /// <summary>Its width in design pixels — a fingertip and a margin, taken from the program's side.</summary>
    public const double StripWidth = 64;

    private readonly WheelSteps _steps = new();
    private Point? _mouseFrom;
    private double _lastY;

    public ScrollStripView(ScrollStripSide side)
    {
        Width = StripWidth;
        IsManipulationEnabled = true;
        BorderThickness = side == ScrollStripSide.Left ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(BorderBrushProperty, "HairlineBrush");

        var grip = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        grip.Children.Add(Chevron(up: true));

        for (var i = 0; i < 3; i++)
        {
            var bar = new Rectangle
            {
                Width = 22,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(0, i == 0 ? 18 : 6, 0, 0),
            };
            bar.SetResourceReference(Shape.FillProperty, "TextLowBrush");
            grip.Children.Add(bar);
        }

        var down = Chevron(up: false);
        down.Margin = new Thickness(0, 18, 0, 0);
        grip.Children.Add(down);

        Child = grip;
    }

    /// <summary>A wheel delta to send (a multiple of 120; positive scrolls up), and the finger's height on the strip.</summary>
    public event Action<int, double>? Scrolled;

    /// <summary>How many notches it has raised, for the occupant's <c>Describe</c>.</summary>
    public int NotchesRaised { get; private set; }

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.TranslateY;
        e.Handled = true;
        _steps.Reset();
        Lit(true);
    }

    protected override void OnManipulationStarted(ManipulationStartedEventArgs e)
    {
        _lastY = e.ManipulationOrigin.Y;
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        // During inertia the origin stops meaning the finger; the last place it was is kept.
        if (!e.IsInertial)
        {
            _lastY = e.ManipulationOrigin.Y;
        }

        Raise(e.DeltaManipulation.Translation.Y, _lastY);
        e.Handled = true;
    }

    protected override void OnManipulationInertiaStarting(ManipulationInertiaStartingEventArgs e)
    {
        // About as a list slows under a flick on a phone: 10 inches per second squared, in DIPs
        // per millisecond squared.
        e.TranslationBehavior.DesiredDeceleration = 10.0 * 96.0 / (1000.0 * 1000.0);
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        Lit(false);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null)
        {
            return;
        }

        _mouseFrom = e.GetPosition(this);
        _steps.Reset();
        CaptureMouse();
        Lit(true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mouseFrom is not { } from || e.StylusDevice is not null)
        {
            return;
        }

        var at = e.GetPosition(this);
        Raise(at.Y - from.Y, at.Y);
        _mouseFrom = at;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_mouseFrom is null)
        {
            return;
        }

        _mouseFrom = null;
        ReleaseMouseCapture();
        Lit(false);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // A wheel over the strip is passed on as it is: the program beside it is what was meant.
        Scrolled?.Invoke(e.Delta, e.GetPosition(this).Y);
        e.Handled = true;
    }

    private void Raise(double travel, double y)
    {
        var delta = _steps.Add(travel);

        if (delta != 0)
        {
            NotchesRaised += Math.Abs(delta / WheelSteps.Notch);
            Scrolled?.Invoke(delta, Math.Clamp(y, 0, ActualHeight));
        }
    }

    private void Lit(bool on) =>
        SetResourceReference(BackgroundProperty, on ? "AccentWashBrush" : "SurfaceBrush");

    private static Path Chevron(bool up)
    {
        var path = new Path
        {
            Data = Geometry.Parse(up ? "M 0,10 L 10,0 L 20,10" : "M 0,0 L 10,10 L 20,0"),
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        path.SetResourceReference(Shape.StrokeProperty, "TextMidBrush");
        return path;
    }
}
