using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DashDeck.Host.Warnings;

/// <summary>
/// A button that acts only when held until it fills — the confirm gesture of ADR-0006 (gate 4).
/// </summary>
/// <remarks>
/// Its command gets how long it was held, as a <see cref="TimeSpan"/>; the choke point checks that
/// again rather than trusting the button. Touch is handled directly, not left to WPF to promote to
/// a mouse press: on glass that promotion is not something to rely on (see CLAUDE.md, the card
/// strip's trap). A mouse press made from a touch is ignored, so one hold is never counted twice.
/// Letting go, or sliding off, before it fills does nothing.
/// </remarks>
public sealed class HoldButton : Border
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(HoldButton));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(HoldButton),
            new PropertyMetadata("HOLD", (d, e) => ((HoldButton)d)._text.Text = (string)e.NewValue));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(HoldButton),
            new PropertyMetadata(Brushes.OrangeRed, (d, e) => ((HoldButton)d)._fill.Background = (Brush)e.NewValue));

    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private long? _pressedAt;

    public HoldButton()
    {
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(_fill);
        grid.Children.Add(_text);
        Child = grid;
        Background = Brushes.Transparent;
        _text.Text = Caption;
        _fill.Background = FillBrush;
        _timer.Tick += (_, _) => Tick();
        IsManipulationEnabled = false;
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>The text's style: font, size, colour.</summary>
    public TextBlock Text => _text;

    /// <summary>How long it must be held. A little over the choke point's own two seconds.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(2.2);

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.StylusDevice is null)
        {
            Begin();
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (e.StylusDevice is null)
        {
            ReleaseMouseCapture();
            Cancel();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (e.StylusDevice is null && !IsMouseCaptured)
        {
            Cancel();
        }
    }

    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        base.OnPreviewTouchDown(e);
        Begin();
        CaptureTouch(e.TouchDevice);
        e.Handled = true;
    }

    protected override void OnPreviewTouchUp(TouchEventArgs e)
    {
        base.OnPreviewTouchUp(e);
        ReleaseTouchCapture(e.TouchDevice);
        Cancel();
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        Cancel();
    }

    private void Begin()
    {
        if (!IsEnabled || Command is null)
        {
            return;
        }

        _pressedAt = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void Cancel()
    {
        _pressedAt = null;
        _timer.Stop();
        _fill.Width = 0;
    }

    private void Tick()
    {
        if (_pressedAt is not { } at)
        {
            Cancel();
            return;
        }

        // A stopwatch on purpose: this measures a finger, not the drive, so IClock has nothing to say.
        var held = Stopwatch.GetElapsedTime(at);
        _fill.Width = ActualWidth * Math.Clamp(held / Hold, 0, 1);

        if (held >= Hold)
        {
            Cancel();
            if (Command is { } command && command.CanExecute(held))
            {
                command.Execute(held);
            }
        }
    }
}
