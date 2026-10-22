using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DashDeck.Core.Phone;

namespace DashDeck.Host.Shell;

/// <summary>
/// Tap, double tap and hold on one element, each its own command (ADR-0075's ANDROID cell).
/// </summary>
/// <remarks>
/// The rules are <see cref="PressGesture"/>'s, in Core and tested; this only feeds it times. Touch is
/// handled as touch and marked handled so it is not promoted to the mouse and counted twice; the
/// mouse is for the desk. A hold fires while the finger is still down, when the time is up, because
/// a menu that waits for the lift feels broken.
/// </remarks>
public static class NavPress
{
    public static readonly DependencyProperty TapCommandProperty = DependencyProperty.RegisterAttached(
        "TapCommand", typeof(ICommand), typeof(NavPress), new PropertyMetadata(null, OnAttached));

    public static readonly DependencyProperty DoubleTapCommandProperty = DependencyProperty.RegisterAttached(
        "DoubleTapCommand", typeof(ICommand), typeof(NavPress), new PropertyMetadata(null));

    public static readonly DependencyProperty HoldCommandProperty = DependencyProperty.RegisterAttached(
        "HoldCommand", typeof(ICommand), typeof(NavPress), new PropertyMetadata(null));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(PressState), typeof(NavPress), new PropertyMetadata(null));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static ICommand? GetTapCommand(DependencyObject d) => (ICommand?)d.GetValue(TapCommandProperty);

    public static void SetTapCommand(DependencyObject d, ICommand? value) => d.SetValue(TapCommandProperty, value);

    public static ICommand? GetDoubleTapCommand(DependencyObject d) => (ICommand?)d.GetValue(DoubleTapCommandProperty);

    public static void SetDoubleTapCommand(DependencyObject d, ICommand? value) => d.SetValue(DoubleTapCommandProperty, value);

    public static ICommand? GetHoldCommand(DependencyObject d) => (ICommand?)d.GetValue(HoldCommandProperty);

    public static void SetHoldCommand(DependencyObject d, ICommand? value) => d.SetValue(HoldCommandProperty, value);

    private static void OnAttached(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || element.GetValue(StateProperty) is not null)
        {
            return;
        }

        var state = new PressState(element);
        element.SetValue(StateProperty, state);

        element.PreviewTouchDown += (_, args) =>
        {
            state.Down();
            args.Handled = true;
        };
        element.PreviewTouchUp += (_, args) =>
        {
            state.Up();
            args.Handled = true;
        };
        element.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.StylusDevice is null)
            {
                state.Down();
                args.Handled = true;
            }
        };
        element.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (args.StylusDevice is null)
            {
                state.Up();
                args.Handled = true;
            }
        };
    }

    private sealed class PressState
    {
        private readonly UIElement _element;
        private readonly DispatcherTimer _hold;
        private TimeSpan? _down;
        private TimeSpan? _lastTap;
        private bool _holdFired;

        public PressState(UIElement element)
        {
            _element = element;
            _hold = new DispatcherTimer { Interval = PressGesture.HoldAfter };
            _hold.Tick += (_, _) =>
            {
                _hold.Stop();
                if (_down is not null)
                {
                    _holdFired = true;
                    _lastTap = null;
                    Run(GetHoldCommand(_element));
                }
            };
        }

        public void Down()
        {
            _down = Clock.Elapsed;
            _holdFired = false;
            _hold.Stop();
            _hold.Start();
        }

        public void Up()
        {
            _hold.Stop();

            if (_down is not { } down)
            {
                return;
            }

            _down = null;
            var up = Clock.Elapsed;

            switch (PressGesture.Released(down, up, _lastTap, _holdFired))
            {
                case PressKind.Tap:
                    _lastTap = up;
                    Run(GetTapCommand(_element));
                    break;
                case PressKind.DoubleTap:
                    _lastTap = null;
                    Run(GetDoubleTapCommand(_element) ?? GetTapCommand(_element));
                    break;
                case PressKind.Hold:
                    _lastTap = null;
                    Run(GetHoldCommand(_element));
                    break;
            }
        }

        private static void Run(ICommand? command)
        {
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }
    }
}
