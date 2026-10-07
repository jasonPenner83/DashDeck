using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Host.Converters;

namespace DashDeck.Host.Stage.Gauges;

/// <summary>
/// Which named states each signal has (ADR-0056), as the running catalog says. Set once at launch,
/// when the catalog is loaded; the catalog does not change until the next launch.
/// </summary>
public static class SignalStates
{
    /// <summary>The states of a signal by id, or null for a number or a signal not in the catalog.</summary>
    public static Func<string, IReadOnlyList<SignalState>?> Of { get; set; } = _ => null;
}

/// <summary>
/// Every state of a multi-state signal in a row, the current one lit — 4WD's 2H 4A 4H 4L, the
/// gear selector's P R N D M (ADR-0056). A value the catalog names no state for lights none and
/// shows itself after a question mark; no reading dims the row with a dash, never a guessed state.
/// </summary>
public sealed class SelectorFace : Grid, IReadingFace
{
    private readonly GaugeSpec _spec;
    private readonly IReadOnlyList<SignalState> _states;
    private readonly List<TextBlock> _words = [];
    private readonly TextBlock _other;
    private readonly Ellipse _dot = new() { Width = 5, Height = 5, Fill = QualityPalette.Unavailable, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private bool _unknown;

    public SelectorFace(GaugeSpec spec, IReadOnlyList<SignalState>? states)
    {
        _spec = spec;
        _states = states ?? [];
        Width = spec.Width;
        Height = spec.Height;

        var size = spec.Number("fontSize", Math.Clamp(spec.Height * 0.5, 10, 40));
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = spec.Text("align", "left").ToLowerInvariant() switch
            {
                "center" => HorizontalAlignment.Center,
                "right" => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
        };

        if (spec.Label.Length > 0)
        {
            var caption = FaceDraw.Label(spec.Text("labelColour", ""), "#8A97A4", spec.Number("labelSize", Math.Max(10, size * 0.42)), "mono", FaceDraw.Weight(spec, "labelWeight", FontWeights.Normal));
            caption.Text = spec.Label;
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(0, 0, spec.Number("gap", size * 0.6), 0);
            row.Children.Add(caption);
        }

        var gap = spec.Number("gap", size * 0.6);
        foreach (var state in _states)
        {
            var word = FaceDraw.Label("", "#5C6670", size, "ui", FaceDraw.Weight(spec, "valueWeight", FontWeights.SemiBold));
            word.Text = state.Name;
            word.Margin = new Thickness(0, 0, gap, 0);
            word.VerticalAlignment = VerticalAlignment.Center;
            _words.Add(word);
            row.Children.Add(word);
        }

        _other = FaceDraw.Label("", "#8A97A4", size, "ui", FaceDraw.Weight(spec, "valueWeight", FontWeights.SemiBold));
        _other.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_other);
        row.Children.Add(_dot);
        Children.Add(row);

        if (_states.Count == 0)
        {
            MarkUnknown("NO STATES");
        }
        else
        {
            Show(new GaugeReading(double.NaN, SignalQuality.Unavailable));
        }
    }

    public void MarkUnknown(string text)
    {
        _unknown = true;
        _other.Text = text;
        _dot.Fill = QualityPalette.Fault;
        Opacity = 0.5;
    }

    public void ShowSource(string source)
    {
    }

    public void Show(GaugeReading reading)
    {
        if (_unknown)
        {
            return;
        }

        _dot.Fill = QualityPalette.For(reading.Quality);
        var showing = reading.HasValue;
        var current = showing ? _states.FirstOrDefault(s => Math.Abs(s.Value - reading.Value) < 1e-6) : null;
        var lit = _spec.Text("litColour", "@accent");
        var unlit = _spec.Text("unlitColour", "");

        for (var i = 0; i < _words.Count; i++)
        {
            var on = ReferenceEquals(_states[i], current);
            GaugeFace.Paint(_words[i], TextBlock.ForegroundProperty, on ? lit : unlit, on ? "@accent" : "#5C6670");
            if (on)
            {
                FaceDraw.Glow(_words[i], _spec.Text("glow", lit), 12);
            }
            else
            {
                _words[i].Effect = null;
            }
        }

        // Honest about the gaps: no reading is a dash, a value with no name is shown as itself.
        _other.Text = !showing ? "–"
            : current is null ? $"?{reading.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}"
            : "";

        Opacity = !showing ? 0.45 : reading.Quality is SignalQuality.Stale ? 0.5 : 1;
    }

    /// <summary>The state lit now, for Describe() and tests; null when none is.</summary>
    public string? Lit(GaugeReading reading) =>
        reading.HasValue ? SignalState.NameOf(_states, reading.Value) : null;
}
