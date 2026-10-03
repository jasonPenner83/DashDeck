using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace TripEconomy;

/// <summary>
/// Trip average fuel economy — the honest number the instantaneous readout can't give.
/// </summary>
/// <remarks>
/// Instantaneous economy swings with every hill; the figure that actually tells you something
/// is the average over the trip, and an average is not a signal you can read off the bus — it
/// is <em>total fuel over total distance</em>, both accumulated over time. So this integrates
/// two signals rather than dividing their current values: distance from <c>vehicle.speed</c>,
/// litres from <c>fuel.flowRate</c> (the truck's fuel rate, or the speed-density estimate, ADR-0041), each against the injected clock, and reports
/// <c>litres ÷ km × 100</c>. Both totals are persisted as they grow and picked back up on the
/// next launch, so a trip survives the app being closed — which, in a truck, is how the app is
/// always closed (its power is pulled).
/// <para>
/// A trip has to be resettable, and that is what the full-screen surface is for: tap the widget
/// and it opens a detail with the distance, the fuel used, the average, and a reset. The widget
/// stays glanceable; the controls live where there is room for them (ADR-0011).
/// </para>
/// </remarks>
public sealed class Component : IDashComponent, IDashComponentView
{
    private const string DistanceKey = "trip.km";
    private const string LitresKey = "trip.litres";

    /// <summary>Below this, an average is noise; show a dash instead of a huge early number.</summary>
    private const double MinDistanceKm = 0.05;

    private IComponentContext _context = null!;
    private ISignalSubscription? _speedDemand;
    private ISignalSubscription? _fuelDemand;
    private IDisposable? _speedSub;
    private IDisposable? _fuelSub;

    private SignalValue _speed;
    private SignalValue _fuel;
    private DateTimeOffset? _lastTickAt;

    private double _distanceKm;
    private double _litres;

    /// <inheritdoc />
    public string Id => "com.jpenner.tripeconomy";

    /// <summary>Litres per 100 km over the trip, or NaN when there is not enough trip to say.</summary>
    private double AverageL100 => _distanceKm >= MinDistanceKm ? _litres / _distanceKm * 100.0 : double.NaN;

    /// <inheritdoc />
    public async Task InitializeAsync(IComponentContext context, CancellationToken ct)
    {
        _context = context;
        _speed = SignalValue.Missing("vehicle.speed");
        _fuel = SignalValue.Missing("fuel.flowRate");

        _distanceKm = await ReadNumber(DistanceKey, ct).ConfigureAwait(false);
        _litres = await ReadNumber(LitresKey, ct).ConfigureAwait(false);

        context.Logger.Log(LogLevel.Info,
            $"trip so far {_distanceKm:0.0} km on {_litres:0.00} L");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        _speedDemand = _context.Signals.Require("vehicle.speed", SignalPriority.Normal, 2);
        _fuelDemand = _context.Signals.Require("fuel.flowRate", SignalPriority.Normal, 2);

        _lastTickAt = null;
        _speedSub = _context.Signals.Subscribe("vehicle.speed", v => { _speed = v; Integrate(); });
        _fuelSub = _context.Signals.Subscribe("fuel.flowRate", v => { _fuel = v; Integrate(); });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        _speedSub?.Dispose();
        _fuelSub?.Dispose();
        _speedDemand?.Dispose();
        _fuelDemand?.Dispose();
        _speedSub = _fuelSub = null;
        _speedDemand = _fuelDemand = null;
        _lastTickAt = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SuspendAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken ct) => StartAsync(ct);

    /// <summary>
    /// Advance both integrals by the time since the last tick, using the values held now.
    /// </summary>
    /// <remarks>
    /// Both inputs must be trustworthy to integrate: if either is Stale or Unavailable the tick
    /// is dropped and the clock is <em>reset</em> rather than paused, so a gap where the truck
    /// was not answering does not later book itself as one enormous interval.
    /// </remarks>
    private async void Integrate()
    {
        var now = _context.Clock.UtcNow;

        if (!_speed.IsUsable || !_fuel.IsUsable)
        {
            _lastTickAt = null;
            return;
        }

        if (_lastTickAt is { } last)
        {
            var hours = (now - last).TotalHours;

            if (hours > 0 && hours < 1)
            {
                _distanceKm += _speed.Value * hours;          // km/h × h = km
                _litres += _fuel.Value * hours;               // L/h × h = L
                await Persist().ConfigureAwait(false);
            }
        }

        _lastTickAt = now;
    }

    private async Task<double> ReadNumber(string key, CancellationToken ct)
    {
        var raw = await _context.Storage.ReadAsync(key, ct).ConfigureAwait(false);
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
    }

    private Task Persist()
    {
        // Two small keys rather than one document: a half-written file loses one number, not both.
        var dist = _context.Storage.WriteAsync(DistanceKey, _distanceKm.ToString("0.####", CultureInfo.InvariantCulture));
        var fuel = _context.Storage.WriteAsync(LitresKey, _litres.ToString("0.####", CultureInfo.InvariantCulture));
        return Task.WhenAll(dist, fuel);
    }

    private async void Reset()
    {
        _distanceKm = 0;
        _litres = 0;
        _lastTickAt = null;
        await Persist().ConfigureAwait(false);
        _context.Logger.Log(LogLevel.Info, "trip reset");
    }

    /// <inheritdoc />
    public FrameworkElement CreateWidget()
    {
        var caption = Caption("AVG ECON");

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
            var avg = AverageL100;
            value.Text = double.IsNaN(avg) ? "—" : avg.ToString("0.0", CultureInfo.CurrentCulture);
            unit.Visibility = double.IsNaN(avg) ? Visibility.Collapsed : Visibility.Visible;
        };
        timer.Start();
        grid.Unloaded += (_, _) => timer.Stop();

        return grid;
    }

    /// <inheritdoc />
    public FrameworkElement CreateFullScreen()
    {
        // Three figures and a reset. Drawn with the host's brushes so it belongs, and refreshed
        // on the same DispatcherTimer idiom as the widget rather than being pushed to.
        var avg = BigFigure(out var avgValue, "L/100km");
        var dist = BigFigure(out var distValue, "km");
        var used = BigFigure(out var usedValue, "L");

        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        rows.Children.Add(Labelled("AVERAGE", avg));
        rows.Children.Add(Labelled("DISTANCE", dist));
        rows.Children.Add(Labelled("FUEL USED", used));

        var reset = new Button
        {
            Content = "RESET TRIP",
            Margin = new Thickness(0, 28, 0, 0),
            Padding = new Thickness(24, 12, 24, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontFamily = Font("MonoFont"),
            FontSize = 15,
            Foreground = Brush("TextHighBrush", Color.FromRgb(0xF2, 0xEF, 0xE9)),
            Background = Brush("SurfaceBrush", Color.FromRgb(0x24, 0x22, 0x1E)),
            BorderBrush = Brush("HairlineBrush", Color.FromRgb(0x3A, 0x37, 0x31)),
            BorderThickness = new Thickness(1),
        };
        reset.Click += (_, _) => Reset();

        var panel = new StackPanel { Margin = new Thickness(40), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(new TextBlock
        {
            Text = "TRIP",
            FontFamily = Font("MonoFont"),
            FontSize = 14,
            Foreground = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E)),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(rows);
        panel.Children.Add(reset);

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var a = AverageL100;
            avgValue.Text = double.IsNaN(a) ? "—" : a.ToString("0.0", CultureInfo.CurrentCulture);
            distValue.Text = _distanceKm.ToString("0.0", CultureInfo.CurrentCulture);
            usedValue.Text = _litres.ToString("0.00", CultureInfo.CurrentCulture);
        };
        timer.Start();
        panel.Unloaded += (_, _) => timer.Stop();

        return panel;
    }

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        FontFamily = Font("MonoFont"),
        FontSize = 12,
        Foreground = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E)),
    };

    private StackPanel BigFigure(out TextBlock value, string unit)
    {
        value = new TextBlock
        {
            FontFamily = Font("UiFont"),
            FontSize = 46,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextHighBrush", Color.FromRgb(0xF2, 0xEF, 0xE9)),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(value);
        row.Children.Add(new TextBlock
        {
            Text = unit,
            FontFamily = Font("MonoFont"),
            FontSize = 16,
            Foreground = Brush("TextMidBrush", Color.FromRgb(0xB8, 0xB3, 0xA8)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 8),
        });
        return row;
    }

    private StackPanel Labelled(string label, FrameworkElement figure)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(Caption(label));
        stack.Children.Add(figure);
        return stack;
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");
}
