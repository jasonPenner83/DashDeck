using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DashDeck.Host.Components;
using DashDeck.Host.Dash;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// A component's widget, placed in the dash as a card (ADR-0023, ADR-0015).
/// </summary>
/// <remarks>
/// The third kind of card source, after the vehicle signal and the tablet sensor. It carries a
/// <see cref="CardSpec"/> whose <c>source</c> is <c>Component</c> and whose id names the
/// component, so it sits in the one ordered arrangement and is saved with the rest. What it
/// draws is the component's own <see cref="Components.LoadedComponent.CreateWidget"/> element —
/// the host never styles a component's insides, only sizes the slot it goes in.
/// <para>
/// <b>The page rule reaches components through here.</b> <see cref="Activate"/> and
/// <see cref="Deactivate"/> start and stop the component behind its guard, so a component whose
/// card is on a page you cannot see spends no request budget — the same rule ADR-0015 applies
/// to signal cards, which is why ADR-0015 required a component to be activatable at all.
/// </para>
/// <para>
/// When the component is not there — a card naming one that is not installed, or one that
/// faulted — the card is kept and marked, never silently dropped (ADR-0015). A missing
/// component reads <c>UNAVAILABLE</c> with its id; a faulted one reads <c>STOPPED</c>.
/// </para>
/// </remarks>
public sealed class ComponentCardViewModel : IDashCard
{
    private readonly LoadedComponent? _component;
    private bool _active;

    public ComponentCardViewModel(CardSpec spec, LoadedComponent? component)
    {
        Spec = spec;
        _component = component;
        Columns = ResolveColumns(component);
        Element = BuildElement();
    }

    /// <inheritdoc />
    public CardSpec Spec { get; }

    /// <inheritdoc />
    public int Columns { get; }

    /// <inheritdoc />
    public double Width => BandGrid.CardWidth(Columns);

    /// <summary>What the dash renders for this card — the component's widget, or a placeholder.</summary>
    public FrameworkElement Element { get; }

    /// <inheritdoc />
    public void Activate()
    {
        if (_active || _component is null)
        {
            return;
        }

        _active = true;

        // Fire and do not await: the guard catches everything, and a card must never block the
        // UI thread waiting on component code. Idempotent because of the flag above, so the
        // many activations a repack triggers do not re-declare the signal each time.
        _ = _component.ActivateAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public void Deactivate()
    {
        if (!_active || _component is null)
        {
            return;
        }

        _active = false;
        _ = _component.DeactivateAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose() => Deactivate();

    /// <summary>The width the component asked for, parsed from its manifest's preferred size.</summary>
    private static int ResolveColumns(LoadedComponent? component)
    {
        var size = component?.Manifest?.Widget.PreferredSize ?? "1x1";
        var width = size.Split('x') is [{ } w, ..] && int.TryParse(w, out var columns) ? columns : 1;
        return Math.Clamp(width, 1, BandGrid.ColumnsPerRow);
    }

    private FrameworkElement BuildElement()
    {
        if (_component is { HasWidget: true } && _component.CreateWidget() is { } widget)
        {
            return widget;
        }

        var message = _component is null
            ? $"COMPONENT\nUNAVAILABLE\n{Spec.SignalId}"
            : "COMPONENT\nSTOPPED";

        return new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x16, 0x14)),
            Child = new TextBlock
            {
                Text = message,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x86, 0x7E)),
            },
        };
    }
}
