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
/// Forwards touch as <em>fractions</em> of the picture as drawn rather than pixels. The dongle's
/// coordinate space is a fixed 0–10000 grid and knows nothing about how large the stage is,
/// so sending WPF coordinates would put every tap near the top-left of the phone's screen and
/// look like the touch layer was being ignored. A touch on the black beside a letterboxed picture
/// goes nowhere — except a lift, so a drag that slides off the picture still ends on the phone.
/// </remarks>
public partial class PhoneLinkView : UserControl
{
    public PhoneLinkView()
    {
        InitializeComponent();

        // Touch first, mouse second, and both — the tablet is a touch device, and every
        // gesture in this shell that was written mouse-first turned out not to work on glass.
        PreviewTouchDown += (_, e) => Send(TouchAction.Down, e.GetTouchPoint(Screen).Position);
        PreviewTouchMove += (_, e) => Send(TouchAction.Move, e.GetTouchPoint(Screen).Position);
        PreviewTouchUp += (_, e) => Send(TouchAction.Up, e.GetTouchPoint(Screen).Position);

        PreviewMouseLeftButtonDown += (_, e) => Send(TouchAction.Down, e.GetPosition(Screen));
        PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton is MouseButtonState.Pressed)
            {
                Send(TouchAction.Move, e.GetPosition(Screen));
            }
        };
        PreviewMouseLeftButtonUp += (_, e) => Send(TouchAction.Up, e.GetPosition(Screen));
    }

    private void Send(TouchAction action, System.Windows.Point point)
    {
        if (DataContext is not PhoneLinkViewModel model || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0)
        {
            return;
        }

        var x = point.X / Screen.ActualWidth;
        var y = point.Y / Screen.ActualHeight;

        if (action is not TouchAction.Up && (x is < 0 or > 1 || y is < 0 or > 1))
        {
            return;
        }

        _ = model.TouchAsync(action, x, y);
    }
}
