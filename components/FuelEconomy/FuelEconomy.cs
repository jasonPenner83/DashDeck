using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace FuelEconomy;

/// <summary>
/// Instantaneous fuel economy — the case for a component rather than a card.
/// </summary>
/// <remarks>
/// <b>A card cannot do this, and that is the point (ADR-0012).</b> A widget is one signal drawn
/// as a number or a bar; fuel economy is <em>two</em> signals combined —
/// <c>engine.fuelRate</c> in litres per hour over <c>vehicle.speed</c> in km/h, which is litres
/// per 100 km. A derived value across signals is exactly what crosses the line from a card into
/// a component.
/// <para>
/// It is honest about the arithmetic. Below a walking pace the figure is meaningless (dividing a
/// fuel rate by a speed near zero runs to infinity), and a Stale or Unavailable reading of
/// either input is not something to compute with — in both cases the widget shows a dash rather
/// than a confident wrong number (rule 3). Moving and fuelling, it shows a lightly smoothed
/// L/100 km, because the raw quotient jitters too much to read at a glance.
/// </para>
/// </remarks>
public sealed class Component : IDashComponent, IDashComponentView
{
    /// <summary>Below this speed the quotient is meaningless, so nothing is shown.</summary>
    private const double MinSpeedKmh = 5.0;

    /// <summary>A plausible ceiling; a truck crawling uphill can post a huge number briefly.</summary>
    private const double MaxDisplay = 99.9;

    /// <summary>Smoothing weight for each new sample. Low enough to settle the glance, high
    /// enough to follow a real change within a second or two.</summary>
    private const double Alpha = 0.25;

    private IComponentContext _context = null!;
    private ISignalSubscription? _speedDemand;
    private ISignalSubscription? _fuelDemand;
    private IDisposable? _speedSub;
    private IDisposable? _fuelSub;

    private SignalValue _speed;
    private SignalValue _fuel;
    private double _smoothed = double.NaN;

    /// <inheritdoc />
    public string Id => "com.jpenner.fueleconomy";

    /// <inheritdoc />
    public Task InitializeAsync(IComponentContext context, CancellationToken ct)
    {
        _context = context;
        _speed = SignalValue.Missing("vehicle.speed");
        _fuel = SignalValue.Missing("engine.fuelRate");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        // Require both signals, so they are polled because of this component, and observe them.
        // A modest 2 Hz: economy is not a number you watch change frame by frame.
        _speedDemand = _context.Signals.Require("vehicle.speed", SignalPriority.Normal, 2);
        _fuelDemand = _context.Signals.Require("engine.fuelRate", SignalPriority.Normal, 2);

        _speedSub = _context.Signals.Subscribe("vehicle.speed", v => { _speed = v; Recompute(); });
        _fuelSub = _context.Signals.Subscribe("engine.fuelRate", v => { _fuel = v; Recompute(); });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        // Withdraw both declarations and stop observing — an unseen component asks for nothing.
        _speedSub?.Dispose();
        _fuelSub?.Dispose();
        _speedDemand?.Dispose();
        _fuelDemand?.Dispose();
        _speedSub = _fuelSub = null;
        _speedDemand = _fuelDemand = null;

        // Forget the smoothed figure, so coming back does not blend a fresh drive into a stale one.
        _smoothed = double.NaN;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SuspendAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken ct) => StartAsync(ct);

    private void Recompute()
    {
        // Both inputs must be trustworthy. If either is Stale or Unavailable, there is no honest
        // number to show, so the display goes blank rather than computing on a guess.
        if (!_speed.IsUsable || !_fuel.IsUsable || _speed.Value < MinSpeedKmh)
        {
            _smoothed = double.NaN;
            return;
        }

        var instant = _fuel.Value / _speed.Value * 100.0;   // L/h ÷ km/h × 100 = L/100 km
        instant = Math.Clamp(instant, 0, MaxDisplay);

        // Exponential smoothing, seeded on the first good sample so it does not crawl up from zero.
        _smoothed = double.IsNaN(_smoothed) ? instant : _smoothed + Alpha * (instant - _smoothed);
    }

    /// <inheritdoc />
    public FrameworkElement CreateWidget()
    {
        // Content only — the host frames the card. Type matched to the dash's own: caption in the
        // mono font and TextLowBrush, the value big in the UI font and TextHighBrush, the unit in
        // TextMidBrush, each read from the host with a fallback.
        var caption = new TextBlock
        {
            Text = "ECONOMY",
            FontFamily = Font("MonoFont"),
            FontSize = 12,
            Foreground = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E)),
        };

        var value = new TextBlock
        {
            FontFamily = Font("UiFont"),
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextHighBrush", Color.FromRgb(0xF2, 0xEF, 0xE9)),
        };

        var unit = new TextBlock
        {
            Text = "L/100km",
            FontFamily = Font("MonoFont"),
            FontSize = 12,
            Foreground = Brush("TextMidBrush", Color.FromRgb(0xB8, 0xB3, 0xA8)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5, 0, 0, 5),
        };

        var number = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        number.Children.Add(value);
        number.Children.Add(unit);
        Grid.SetRow(number, 1);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(caption);
        grid.Children.Add(number);

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            // A dash when there is no honest figure — stopped, or a stale input.
            value.Text = double.IsNaN(_smoothed)
                ? "—"
                : _smoothed.ToString("0.0", CultureInfo.CurrentCulture);
            unit.Visibility = double.IsNaN(_smoothed) ? Visibility.Collapsed : Visibility.Visible;
        };
        timer.Start();
        grid.Unloaded += (_, _) => timer.Stop();

        return grid;
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");
}
