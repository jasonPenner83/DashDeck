using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Views;

/// <summary>
/// The dash: pages of cards, and the gestures that move and rearrange them.
/// </summary>
/// <remarks>
/// <b>Slid, not scrolled.</b> A <c>ScrollViewer</c> would give free kinetic scrolling, and
/// free scrolling is wrong here for two reasons that only show up in a moving vehicle: the
/// dash slides under your finger over every bump, and a page can come to rest half way, at
/// which point a card is half off the edge and "which page am I on" stops having an answer.
/// It also stops having an answer for the code — and the code needs one, because the page you
/// are on is what decides which cards hold live signal declarations.
/// <para>
/// So the strip is a plain <see cref="StackPanel"/> under a
/// <see cref="System.Windows.Media.TranslateTransform"/>: dragging moves it, letting go snaps
/// it to exactly one page, and the view-model is told which.
/// </para>
/// <para>
/// This is view mechanics rather than shell logic, which is why it lives in a code-behind
/// (ADR-0011). Nothing here decides anything about cards or signals; it converts finger
/// movement into a page number and hands it over.
/// </para>
/// </remarks>
public partial class DashboardView : UserControl
{
    /// <summary>One page is one design width, so the strip slides by exactly this.</summary>
    private const double PageStride = BandGrid.DesignWidth;

    /// <summary>How far a drag must go before it counts as a page turn rather than a tap.</summary>
    private const double TurnThreshold = PageStride * 0.18;

    /// <summary>
    /// How far a finger may wander before a press counts as a drag rather than a tap.
    /// </summary>
    /// <remarks>
    /// This used to also be the tolerance for a 600 ms hold that entered edit mode. That
    /// gesture is gone: it was built on the mouse events, and this element has manipulation
    /// enabled for swiping — which consumes touch before WPF ever promotes it to a mouse
    /// event, so the hold could never fire from a finger. It was replaced by the overflow
    /// menu in the status strip rather than repaired, because a hidden gesture nobody can
    /// discover is not much better than one that does not work.
    /// </remarks>
    private const double DragSlop = 12;

    private DashboardViewModel? _model;
    private Point _pressedAt;
    private bool _dragging;
    private double _dragFrom;

    public DashboardView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();

        Root.IsManipulationEnabled = true;
        Root.ManipulationStarting += OnManipulationStarting;
        Root.ManipulationDelta += OnManipulationDelta;
        Root.ManipulationCompleted += OnManipulationCompleted;

        Root.PreviewMouseLeftButtonDown += OnPressed;
        Root.PreviewMouseMove += OnMoved;
        Root.PreviewMouseLeftButtonUp += OnReleased;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        _model = DataContext as DashboardViewModel;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            SlideTo(_model.PageIndex, animate: false);
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
        // Pages rebuilt under us — a card added, removed, resized, or the stage taking a
        // different number of bands — so the strip has to be re-seated even if the page
        // number did not change.
        if (e.PropertyName is nameof(DashboardViewModel.PageIndex))
        {
            SlideTo(_model?.PageIndex ?? 0, animate: true);
        }
        else if (e.PropertyName is nameof(DashboardViewModel.Pages)
                 or nameof(DashboardViewModel.HasMultiplePages))
        {
            SlideTo(_model?.PageIndex ?? 0, animate: false);
        }
    }

    /// <summary>Put the strip where a page number says it should be.</summary>
    private void SlideTo(int pageIndex, bool animate)
    {
        var target = -pageIndex * PageStride;

        if (!animate)
        {
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            Slide.X = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }

    /// <summary>
    /// Decide which page a drag ended on.
    /// </summary>
    /// <remarks>
    /// A short drag springs back to where it started rather than turning the page. That
    /// matters more than it sounds: the cards in edit mode carry buttons, and a thumb that
    /// moves four pixels while pressing one must not also turn the page under it.
    /// </remarks>
    private void SettleAfterDrag(double offset)
    {
        if (_model is null)
        {
            return;
        }

        var page = _model.PageIndex;

        if (offset <= -TurnThreshold)
        {
            page = Math.Min(page + 1, _model.Pages.Count - 1);
        }
        else if (offset >= TurnThreshold)
        {
            page = Math.Max(page - 1, 0);
        }

        if (page == _model.PageIndex)
        {
            // Unchanged, so nothing will raise PropertyChanged and nothing would put the
            // half-dragged strip back.
            SlideTo(page, animate: true);
        }
        else
        {
            _model.GoToPageCommand.Execute(page);
        }
    }

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Root;
        e.Mode = ManipulationModes.TranslateX;
        _dragFrom = Slide.X;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Slide.X = _dragFrom + e.CumulativeManipulation.Translation.X;
    }

    private void OnManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        // A press that barely moved is a tap, not a page turn — and this is the one place a tap
        // has to be handled by hand. The strip has manipulation enabled for swiping, which
        // consumes touch before WPF promotes it to a mouse click, so a Button on a card never
        // fires from a finger (the same trap that killed the old hold gesture). Mouse clicks
        // still reach the buttons directly; touch taps are routed here instead.
        if (Math.Abs(e.TotalManipulation.Translation.X) < DragSlop)
        {
            HandleTap(e.ManipulationOrigin);
        }

        SettleAfterDrag(e.TotalManipulation.Translation.X);
    }

    /// <summary>
    /// Dev-only: run the tap path against the first component card that offers a detail, so the
    /// touch routing can be exercised without a touch screen (the <c>--tap-detail</c> flag).
    /// </summary>
    public bool TapFirstDetailCard()
    {
        if (FindDetailButton(Root) is not { } button)
        {
            return false;
        }

        var centre = button.TransformToVisual(Root).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        HandleTap(centre);
        return true;
    }

    private static Button? FindDetailButton(DependencyObject node)
    {
        if (node is Button { IsVisible: true } b && b.DataContext is ComponentCardViewModel { HasFullScreen: true })
        {
            return b;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);

        for (var i = 0; i < count; i++)
        {
            if (FindDetailButton(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Fire the command of whatever button a tap landed on — the click a touch never became.
    /// </summary>
    /// <remarks>
    /// Deliberately generic: it finds the topmost <see cref="Button"/> under the point and runs
    /// its command, so it works for every tap target on a card — a component's open-detail
    /// surface, and in edit mode the open-editor surface, the remove badge and the reorder
    /// chevrons — without this code knowing which is which. That is exactly what a mouse click
    /// does; this just makes touch do the same. <see cref="Panel.IsHitTestVisible"/> and
    /// visibility are respected by the hit test, so a collapsed edit overlay is never hit and a
    /// plain signal card, which carries no button when not editing, does nothing.
    /// </remarks>
    private void HandleTap(Point origin)
    {
        var hit = Root.InputHitTest(origin) as DependencyObject;

        while (hit is not null and not Button)
        {
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }

        if (hit is Button { Command: { } command } button && command.CanExecute(button.CommandParameter))
        {
            command.Execute(button.CommandParameter);
        }
    }

    /// <summary>
    /// Mouse dragging, for development.
    /// </summary>
    /// <remarks>
    /// The tablet is a touch device and manipulation events cover it, but every one of these
    /// gestures otherwise needs a Surface to try — which is exactly the sort of thing that
    /// only gets tested once, on the truck, in the dark.
    /// </remarks>
    private void OnPressed(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(Root);
        _dragFrom = Slide.X;
        _dragging = false;
    }

    private void OnMoved(object sender, MouseEventArgs e)
    {
        if (e.LeftButton is not MouseButtonState.Pressed)
        {
            return;
        }

        var offset = e.GetPosition(Root).X - _pressedAt.X;

        if (!_dragging && Math.Abs(offset) < DragSlop)
        {
            return;
        }

        _dragging = true;

        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Slide.X = _dragFrom + offset;
    }

    private void OnReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        SettleAfterDrag(e.GetPosition(Root).X - _pressedAt.X);
    }

}
