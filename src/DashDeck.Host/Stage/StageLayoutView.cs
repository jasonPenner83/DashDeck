using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;
using DashDeck.Host.Stage.Gauges;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Stage;

/// <summary>
/// Draws a <see cref="StageLayout"/> on the stage (ADR-0037): every element at its position on a
/// 912 × 636 canvas, scaled to whatever the stage really is.
/// </summary>
/// <remarks>
/// Owns the signal declarations for the gauges it draws, so a gauge costs request budget only while
/// it is on screen (ADR-0004), and redraws from scratch when the layout changes or is reloaded —
/// which is also what drops the old declarations.
/// </remarks>
public sealed class StageLayoutView : UserControl, IDisposable
{
    private readonly StageLayoutService _layouts;
    private readonly IVehicleSignals _signals;
    private readonly IClock _clock;
    private readonly Canvas _canvas = new() { Width = StageLayout.StageWidth, Height = StageLayout.StageHeight, ClipToBounds = true };
    private readonly List<IDisposable> _owned = [];
    private readonly List<(GaugeSpec Spec, Func<GaugeReading> Read)> _gauges = [];
    private readonly DispatcherTimer _tick;
    private readonly List<(TextBlock Text, string Format)> _clocks = [];

    public StageLayoutView(StageLayoutService layouts, IVehicleSignals signals, IClock clock)
    {
        _layouts = layouts;
        _signals = signals;
        _clock = clock;

        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => TickClocks();
        _tick.Start();

        _layouts.LayoutChanged += OnLayoutChanged;
        Build(_layouts.Current);
    }

    /// <summary>A line for the occupant's Describe(): the layout, and each gauge's reading.</summary>
    public string Describe() =>
        $"layout={_layouts.Current.Id} " + string.Join(' ', _gauges.Select(g =>
        {
            var r = g.Read();
            return $"{(g.Spec.Id.Length > 0 ? g.Spec.Id : g.Spec.Label)}={(r.HasValue ? r.Value.ToString("0.#", CultureInfo.InvariantCulture) : "-")}/{r.Quality}";
        }));

    private void OnLayoutChanged(object? sender, EventArgs e) => Dispatcher.Invoke(() => Build(_layouts.Current));

    private void Build(StageLayout layout)
    {
        Release();
        _canvas.Children.Clear();

        Paint(_canvas, Panel.BackgroundProperty, layout.Background, "@canvas");

        foreach (var element in layout.Elements)
        {
            var visual = element.Type switch
            {
                StageElementType.Gauge => Gauge(element),
                StageElementType.Text => Text(element, element.Content),
                StageElementType.Clock => Clock(element),
                _ => PanelShape(element),
            };

            Canvas.SetLeft(visual, element.X);
            Canvas.SetTop(visual, element.Y);
            _canvas.Children.Add(visual);
        }

        TickClocks();
    }

    private FrameworkElement Gauge(GaugeSpec spec)
    {
        var face = new GaugeFace(spec);
        var primary = Observe(spec.Source.Signal, spec.Source.RateHz);
        var minus = spec.Source.Minus is { } m ? Observe(m, Math.Max(0.2, spec.Source.RateHz / 4)) : null;

        if (primary is null || (spec.Source.Minus is not null && minus is null && spec.Source.MinusFallback is null))
        {
            // Not in the catalog. The gauge says so rather than drawing a scale for nothing.
            face.MarkUnknown();
            _gauges.Add((spec, () => new GaugeReading(double.NaN, SignalQuality.Unavailable)));
            return face;
        }

        GaugeReading Read() => GaugeReading.Compute(spec.Source, primary.Current, minus?.Current);

        void Update(object? sender, PropertyChangedEventArgs e) => face.Show(Read());

        primary.PropertyChanged += Update;
        if (minus is not null)
        {
            minus.PropertyChanged += Update;
        }

        _gauges.Add((spec, Read));
        face.Show(Read());
        return face;
    }

    /// <summary>Declare one signal for as long as this layout is up, or null for one the catalog lacks.</summary>
    private ObservableSignal? Observe(string signalId, double rateHz)
    {
        try
        {
            var signal = new ObservableSignal(_signals, signalId, SignalPriority.Normal, rateHz);
            _owned.Add(signal);
            return signal;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static TextBlock Text(GaugeSpec spec, string content)
    {
        var text = new TextBlock
        {
            Text = content,
            Width = spec.Width,
            Height = spec.Height,
            FontSize = spec.FontSize,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = spec.Align.ToLowerInvariant() switch
            {
                "center" or "centre" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };

        text.SetResourceReference(TextBlock.FontFamilyProperty, spec.Font.Equals("ui", StringComparison.OrdinalIgnoreCase) ? "UiFont" : "MonoFont");
        Paint(text, TextBlock.ForegroundProperty, spec.Colour, "@textHigh");
        return text;
    }

    private TextBlock Clock(GaugeSpec spec)
    {
        var text = Text(spec, "");
        _clocks.Add((text, spec.Content.Length > 0 ? spec.Content : "HH:mm"));
        return text;
    }

    private static Border PanelShape(GaugeSpec spec)
    {
        var r = StageLayout.ParseRadius(spec.Radius) ?? [0, 0, 0, 0];
        var panel = new Border
        {
            Width = spec.Width,
            Height = spec.Height,
            CornerRadius = new CornerRadius(r[0], r[1], r[2], r[3]),
        };

        Paint(panel, Border.BackgroundProperty, spec.Colour, "@surface");
        return panel;
    }

    private void TickClocks()
    {
        // IClock, never DateTime.Now — a replayed drive shows the time it happened.
        var now = _clock.UtcNow.ToLocalTime();
        foreach (var (text, format) in _clocks)
        {
            text.Text = now.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    /// <summary>A layout colour: <c>@token</c> as a live theme reference, <c>#RRGGBB</c> as itself.</summary>
    private static void Paint(FrameworkElement target, DependencyProperty property, string? colour, string fallback)
    {
        foreach (var candidate in new[] { colour, fallback })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.StartsWith('@') && ThemeTokens.TryGet(candidate[1..], out var token) && token.Kind is TokenKind.Colour)
            {
                target.SetResourceReference(property, token.ResourceKey);
                return;
            }

            if (ThemeColour.TryParse(candidate, out var c))
            {
                var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
                brush.Freeze();
                target.SetValue(property, brush);
                return;
            }
        }
    }

    /// <summary>Drop every signal declaration this layout made.</summary>
    private void Release()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _owned.Clear();
        _gauges.Clear();
        _clocks.Clear();
    }

    public void Dispose()
    {
        _tick.Stop();
        _layouts.LayoutChanged -= OnLayoutChanged;
        Release();
    }
}
