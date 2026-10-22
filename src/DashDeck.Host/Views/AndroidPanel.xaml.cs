using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Views;

/// <summary>
/// Android's screen, full screen or on the stage (ADR-0075). Bound to <see cref="AndroidViewModel"/>.
/// </summary>
/// <remarks>
/// One view for both placements, like the map's. Being on screen is what starts Android and its
/// frames: the panel tells the view-model when it arrives and when it goes, and the view-model
/// counts, because a placement change briefly has two panels alive.
/// <para>
/// Touch goes as <em>fractions</em> of the picture as drawn, for the reason the phone link gives
/// (ADR-0057): the device's pixels are not the stage's. A touch beside a letterboxed picture goes
/// nowhere, except a lift, so a drag that slides off the picture still ends on the device.
/// </para>
/// <para>
/// The panel also says how big it is, in screen pixels, so frames are asked for at the size they are
/// drawn and a tablet can be turned to the region's shape.
/// </para>
/// </remarks>
public partial class AndroidPanel : UserControl
{
    private AndroidViewModel? _attached;

    public AndroidPanel()
    {
        InitializeComponent();

        Loaded += (_, _) => Attach(DataContext as AndroidViewModel);
        Unloaded += (_, _) => Attach(null);
        SizeChanged += (_, _) => ReportSize();
        DataContextChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                Attach(DataContext as AndroidViewModel);
            }
        };

        // Touch first and handled, so it is not also promoted to the mouse and sent twice; the mouse
        // for the desk.
        PreviewTouchDown += (_, e) =>
        {
            Send(e.GetTouchPoint(Screen).Position, down: true);
            e.Handled = true;
        };
        PreviewTouchMove += (_, e) =>
        {
            Send(e.GetTouchPoint(Screen).Position, down: true);
            e.Handled = true;
        };
        PreviewTouchUp += (_, e) =>
        {
            Send(e.GetTouchPoint(Screen).Position, down: false);
            e.Handled = true;
        };

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice is null)
            {
                Send(e.GetPosition(Screen), down: true);
            }
        };
        PreviewMouseMove += (_, e) =>
        {
            if (e.StylusDevice is null && e.LeftButton is MouseButtonState.Pressed)
            {
                Send(e.GetPosition(Screen), down: true);
            }
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.StylusDevice is null)
            {
                Send(e.GetPosition(Screen), down: false);
            }
        };
    }

    private void Attach(AndroidViewModel? model)
    {
        if (ReferenceEquals(model, _attached))
        {
            return;
        }

        _attached?.Detach();
        _attached = model;
        ReportSize();
        _attached?.Attach();
    }

    private void ReportSize()
    {
        if (_attached is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _attached.Resize((int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY), dpi.DpiScaleX);
    }

    private void Send(Point point, bool down)
    {
        if (_attached is not { IsPictureShowing: true } model || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0)
        {
            return;
        }

        model.Touch(point.X / Screen.ActualWidth, point.Y / Screen.ActualHeight, down);
    }
}
