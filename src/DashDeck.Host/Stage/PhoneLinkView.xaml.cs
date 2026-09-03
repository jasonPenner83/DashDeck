using System.Windows.Controls;
using System.Windows.Input;
using DashDeck.Host.ViewModels;

// System.Windows.Input has a TouchAction of its own, and it means something else entirely.
// Aliased so the ambiguity cannot quietly resolve the wrong way.
using TouchAction = DashDeck.Host.PhoneLink.TouchAction;

namespace DashDeck.Host.Stage;

/// <summary>
/// The phone-link stage. Bound to <see cref="PhoneLinkViewModel"/>.
/// </summary>
/// <remarks>
/// Forwards touch as <em>fractions</em> of its own surface rather than pixels. The dongle's
/// coordinate space is a fixed 0–10000 grid and knows nothing about how large the stage is,
/// so sending WPF coordinates would put every tap near the top-left of the phone's screen and
/// look like the touch layer was being ignored.
/// </remarks>
public partial class PhoneLinkView : UserControl
{
    public PhoneLinkView()
    {
        InitializeComponent();

        // Touch first, mouse second, and both — the tablet is a touch device, and every
        // gesture in this shell that was written mouse-first turned out not to work on glass.
        PreviewTouchDown += (_, e) => Send(TouchAction.Down, e.GetTouchPoint(this).Position);
        PreviewTouchMove += (_, e) => Send(TouchAction.Move, e.GetTouchPoint(this).Position);
        PreviewTouchUp += (_, e) => Send(TouchAction.Up, e.GetTouchPoint(this).Position);

        PreviewMouseLeftButtonDown += (_, e) => Send(TouchAction.Down, e.GetPosition(this));
        PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton is MouseButtonState.Pressed)
            {
                Send(TouchAction.Move, e.GetPosition(this));
            }
        };
        PreviewMouseLeftButtonUp += (_, e) => Send(TouchAction.Up, e.GetPosition(this));
    }

    private void Send(TouchAction action, System.Windows.Point point)
    {
        if (DataContext is not PhoneLinkViewModel model || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _ = model.TouchAsync(action, point.X / ActualWidth, point.Y / ActualHeight);
    }
}
