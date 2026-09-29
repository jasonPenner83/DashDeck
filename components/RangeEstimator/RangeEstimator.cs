using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace RangeEstimator;

/// <summary>
/// Distance to empty — how far the truck can go on the fuel that is left.
/// </summary>
/// <remarks>
/// <b>Three signals, not one, so it is a component (ADR-0012).</b> It combines the fuel level
/// with a recent economy it works out itself from fuel rate over speed — a derived value across
/// signals, which is exactly the line a card cannot cross.
/// <para>
/// It is deliberately steady rather than twitchy: range is a number you plan a stop around, so
/// the economy it divides by is smoothed slowly and seeded with a plausible figure, and it holds
/// the last learned economy while stopped rather than throwing the estimate away. It is honest
/// about the one thing it cannot measure — the tank's size, which varies by build — so that is a
/// documented constant, and it shows a dash rather than a guess when the fuel level is not
/// trustworthy (rule 3).
/// </para>
/// </remarks>
public sealed class Component : IDashComponent, IDashComponentView
{
    /// <summary>
    /// Fallback tank, litres, for a host that supplies no vehicle profile (apiVersion &lt; 1.1).
    /// The real value comes from Settings ▸ Vehicle via <see cref="IComponentContext.Vehicle"/>
    /// (ADR-0029); this is Jason's 36 US-gallon tank (≈ 136 L) in case it does not.
    /// </summary>
    private const double FallbackTankLitres = 136.0;

    /// <summary>Below this the fuel-rate-over-speed quotient is meaningless, so economy is not learned.</summary>
    private const double MinSpeedKmh = 5.0;

    /// <summary>A plausible F-150 combined figure, so a range shows the moment there is fuel to divide.</summary>
    private const double SeedEconomyL100 = 14.0;

    private const double MaxEconomyL100 = 40.0;

    /// <summary>Slow smoothing: range should settle, not follow every hill.</summary>
    private const double Alpha = 0.12;

    private IComponentContext _context = null!;
    private ISignalSubscription? _speedDemand;
    private ISignalSubscription? _fuelRateDemand;
    private ISignalSubscription? _levelDemand;
    private IDisposable? _speedSub;
    private IDisposable? _fuelRateSub;
    private IDisposable? _levelSub;

    private SignalValue _speed;
    private SignalValue _fuelRate;
    private SignalValue _level;
    private double _economy = SeedEconomyL100;

    /// <inheritdoc />
    public string Id => "com.jpenner.rangeestimator";

    /// <inheritdoc />
    public Task InitializeAsync(IComponentContext context, CancellationToken ct)
    {
        _context = context;
        _speed = SignalValue.Missing("vehicle.speed");
        _fuelRate = SignalValue.Missing("engine.fuelRate");
        _level = SignalValue.Missing("fuel.levelPercent");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        _speedDemand = _context.Signals.Require("vehicle.speed", SignalPriority.Normal, 2);
        _fuelRateDemand = _context.Signals.Require("engine.fuelRate", SignalPriority.Normal, 2);

        // Normal, not Low: the range depends on the fuel level, and Low is the priority that gets
        // starved first under a heavy demand plan. It is only 0.2 Hz, so the budget cost is nil —
        // this is about being scheduled at all, not about rate.
        _levelDemand = _context.Signals.Require("fuel.levelPercent", SignalPriority.Normal, 0.2);

        _speedSub = _context.Signals.Subscribe("vehicle.speed", v => { _speed = v; LearnEconomy(); });
        _fuelRateSub = _context.Signals.Subscribe("engine.fuelRate", v => { _fuelRate = v; LearnEconomy(); });
        _levelSub = _context.Signals.Subscribe("fuel.levelPercent", v => _level = v);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        _speedSub?.Dispose();
        _fuelRateSub?.Dispose();
        _levelSub?.Dispose();
        _speedDemand?.Dispose();
        _fuelRateDemand?.Dispose();
        _levelDemand?.Dispose();
        _speedSub = _fuelRateSub = _levelSub = null;
        _speedDemand = _fuelRateDemand = _levelDemand = null;

        // Forget the learned economy, so returning does not blend a fresh drive into a stale one.
        _economy = SeedEconomyL100;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SuspendAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken ct) => StartAsync(ct);

    private void LearnEconomy()
    {
        // Only while actually driving and fuelling: a stopped truck divides by ~0, and a stale
        // reading of either input is not worth learning from.
        if (!_speed.IsUsable || !_fuelRate.IsUsable || _speed.Value < MinSpeedKmh)
        {
            return;
        }

        var instant = Math.Clamp(_fuelRate.Value / _speed.Value * 100.0, 0.1, MaxEconomyL100);
        _economy += Alpha * (instant - _economy);
    }

    /// <summary>The tank size the user set in Settings ▸ Vehicle, or the fallback if none.</summary>
    private double TankLitres()
    {
        var fromProfile = _context.Vehicle.FuelTankLitres;
        return fromProfile > 0 ? fromProfile : FallbackTankLitres;
    }

    private double LitresRemaining =>
        _level.IsUsable ? Math.Clamp(_level.Value, 0, 100) / 100.0 * TankLitres() : double.NaN;

    private double RangeKm
    {
        get
        {
            var litres = LitresRemaining;
            return double.IsNaN(litres) || _economy <= 0 ? double.NaN : litres * 100.0 / _economy;
        }
    }

    /// <inheritdoc />
    public FrameworkElement CreateWidget()
    {
        var caption = new TextBlock
        {
            Text = "RANGE",
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
            Text = "km",
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
            var range = RangeKm;
            value.Text = double.IsNaN(range) ? "—" : range.ToString("0", CultureInfo.CurrentCulture);
            unit.Visibility = double.IsNaN(range) ? Visibility.Collapsed : Visibility.Visible;
        };
        timer.Start();
        grid.Unloaded += (_, _) => timer.Stop();

        return grid;
    }

    /// <inheritdoc />
    public FrameworkElement CreateFullScreen()
    {
        // The estimate broken down, so the number on the card can be trusted or doubted.
        var range = BigFigure(out var rangeValue, "km");
        var fuel = BigFigure(out var fuelValue, "%");
        var tank = BigFigure(out var tankValue, "L");
        var econ = BigFigure(out var econValue, "L/100km");

        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        rows.Children.Add(Labelled("RANGE", range));
        rows.Children.Add(Labelled("FUEL LEFT", fuel));
        rows.Children.Add(Labelled("IN THE TANK", tank));
        rows.Children.Add(Labelled("USING", econ));

        var panel = new StackPanel { Margin = new Thickness(40), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(new TextBlock
        {
            Text = "RANGE TO EMPTY",
            FontFamily = Font("MonoFont"),
            FontSize = 14,
            Foreground = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E)),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(rows);
        panel.Children.Add(new TextBlock
        {
            Text = $"Assuming a {TankLitres():0} L tank and recent economy. Set the tank size in Settings ▸ Vehicle.",
            FontFamily = Font("MonoFont"),
            FontSize = 12,
            MaxWidth = 520,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextFaintBrush", Color.FromRgb(0x6A, 0x67, 0x60)),
            Margin = new Thickness(0, 28, 0, 0),
        });

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var r = RangeKm;
            var litres = LitresRemaining;

            rangeValue.Text = double.IsNaN(r) ? "—" : r.ToString("0", CultureInfo.CurrentCulture);
            fuelValue.Text = _level.IsUsable ? _level.Value.ToString("0", CultureInfo.CurrentCulture) : "—";
            tankValue.Text = double.IsNaN(litres) ? "—" : litres.ToString("0.0", CultureInfo.CurrentCulture);
            econValue.Text = _economy.ToString("0.0", CultureInfo.CurrentCulture);
        };
        timer.Start();
        panel.Unloaded += (_, _) => timer.Stop();

        return panel;
    }

    /// <summary>A big number and its unit, with the value returned for the timer to fill.</summary>
    private StackPanel BigFigure(out TextBlock value, string unit)
    {
        value = new TextBlock
        {
            FontFamily = Font("UiFont"),
            FontSize = 40,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextHighBrush", Color.FromRgb(0xF2, 0xEF, 0xE9)),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        row.Children.Add(value);
        row.Children.Add(new TextBlock
        {
            Text = unit,
            FontFamily = Font("MonoFont"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 8),
            Foreground = Brush("TextMidBrush", Color.FromRgb(0xB8, 0xB3, 0xA8)),
        });

        return row;
    }

    /// <summary>A caption over a figure, one block in the column.</summary>
    private StackPanel Labelled(string label, FrameworkElement figure)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        block.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = Font("MonoFont"),
            FontSize = 12,
            Foreground = Brush("TextLowBrush", Color.FromRgb(0x8A, 0x86, 0x7E)),
        });
        block.Children.Add(figure);
        return block;
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");
}
