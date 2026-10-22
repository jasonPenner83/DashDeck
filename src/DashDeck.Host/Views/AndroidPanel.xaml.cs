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
        SizeChanged += (_, _) => PlaceKeys();
        ScreenHost.SizeChanged += (_, _) => ReportSize();
        DataContextChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                Attach(DataContext as AndroidViewModel);
            }
        };

        // Touch first and handled, so it is not also promoted to the mouse and sent twice; the mouse
        // for the desk. Only on the picture's own area: a press on the key bar is the bar's.
        ScreenHost.PreviewTouchDown += (_, e) =>
        {
            Send(e.GetTouchPoint(Screen).Position, down: true);
            e.Handled = true;
        };
        ScreenHost.PreviewTouchMove += (_, e) =>
        {
            Send(e.GetTouchPoint(Screen).Position, down: true);
            e.Handled = true;
        };
        ScreenHost.PreviewTouchUp += (_, e) =>
        {
            Send(e.GetTouchPoint(Screen).Position, down: false);
            e.Handled = true;
        };

        ScreenHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice is null)
            {
                Send(e.GetPosition(Screen), down: true);
            }
        };
        ScreenHost.PreviewMouseMove += (_, e) =>
        {
            if (e.StylusDevice is null && e.LeftButton is MouseButtonState.Pressed)
            {
                Send(e.GetPosition(Screen), down: true);
            }
        };
        ScreenHost.PreviewMouseLeftButtonUp += (_, e) =>
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

    /// <summary>The picture's own area — the panel less the key bar — is what Android is fitted to.</summary>
    private void ReportSize()
    {
        if (_attached is null || ScreenHost.ActualWidth <= 0 || ScreenHost.ActualHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _attached.Resize(
            (int)Math.Round(ScreenHost.ActualWidth * dpi.DpiScaleX), (int)Math.Round(ScreenHost.ActualHeight * dpi.DpiScaleY), dpi.DpiScaleX);
    }

    /// <summary>
    /// The key bar along the bottom of a tall panel, down the right of a wide one — the side the
    /// picture can best spare.
    /// </summary>
    private void PlaceKeys()
    {
        const double bar = 64;
        var wide = ActualWidth > ActualHeight;

        Grid.SetRow(KeyBar, wide ? 0 : 1);
        Grid.SetColumn(KeyBar, wide ? 1 : 0);
        KeyRow.Height = new GridLength(wide ? 0 : bar);
        KeyColumn.Width = new GridLength(wide ? bar : 0);
        KeyBar.BorderThickness = wide ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        KeyGrid.Rows = wide ? 0 : 1;
        KeyGrid.Columns = wide ? 1 : 0;
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
