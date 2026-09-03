using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;
using DashDeck.Host.Dash;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// One card on the dash: a spec, whatever supplies it, and what it draws.
/// </summary>
/// <remarks>
/// Two things distinguish this from the fixed widgets it replaced.
/// <para>
/// <b>It can be switched off.</b> <see cref="Activate"/> creates the value and
/// <see cref="Deactivate"/> disposes it, and the dashboard only activates the page you are
/// looking at. For a vehicle signal that is demand on one serialised link whose real ceiling
/// is unmeasured (Q12), so thirty cards all declaring at once would degrade the six actually
/// in front of you. A card you cannot see asks for nothing.
/// </para>
/// <para>
/// <b>It does not know where its number comes from.</b> A signal and a tablet sensor arrive
/// through the same <see cref="ICardValue"/>, which is what let B4 happen without the card,
/// the packer or the pages learning about a second catalog — and is the shape a component's
/// own source will arrive in later (F3).
/// </para>
/// </remarks>
public sealed partial class WidgetCardViewModel : ObservableObject, IDashSlot, IDisposable
{
    private readonly CardValueFactory _values;
    private ICardValue? _value;
    private bool _disposed;

    public WidgetCardViewModel(CardValueFactory values, CardSpec spec, ValueChoice? choice)
    {
        _values = values;
        Spec = spec;
        Choice = choice;
    }

    /// <summary>What this card is, as stored.</summary>
    public CardSpec Spec { get; private set; }

    /// <summary>Catalog metadata for whatever it is bound to, or null if nothing defines it.</summary>
    public ValueChoice? Choice { get; private set; }

    /// <summary>
    /// True when the card names something neither catalog defines.
    /// </summary>
    /// <remarks>
    /// Kept and rendered as broken rather than dropped. ADR-0012 asks widget validation to
    /// fail loudly, and quietly deleting a card because a catalog file moved is the loudest
    /// possible failure to notice and the worst possible one to recover from. A signal card
    /// also cannot simply be activated: the arbiter throws on an unknown id, by design.
    /// </remarks>
    public bool IsMissing => Choice is null;

    /// <summary>The caption. Falls back to the catalog name, then to the raw id.</summary>
    public string Label => !string.IsNullOrWhiteSpace(Spec.Label)
        ? Spec.Label
        : Choice?.Caption ?? Spec.SignalId.ToUpperInvariant();

    /// <inheritdoc />
    public int Columns => Spec.Columns;

    /// <inheritdoc />
    public double Width => BandGrid.CardWidth(Columns);

    /// <summary>True when the value should be drawn as a bar as well as a number.</summary>
    public bool IsBar => Spec.ParsedStyle is CardStyle.Bar && Choice?.HasRange is true;

    /// <summary>How much this can be trusted. Missing is Unavailable, honestly.</summary>
    public SignalQuality Quality => _value?.Quality ?? SignalQuality.Unavailable;

    /// <summary>The value, converted and formatted, or the placeholder.</summary>
    public string Text
    {
        get
        {
            if (IsMissing || _value is not { IsUsable: true } value || double.IsNaN(value.Value))
            {
                return ObservableSignal.Placeholder;
            }

            return DisplayUnits.Format(value.Value, value.Unit, Spec.ParsedUnit, Spec.Format);
        }
    }

    /// <summary>The unit symbol as displayed, which is not always the one the source sent.</summary>
    public string UnitText => IsMissing
        ? "no such value"
        : DisplayUnits.Symbol(Spec.ParsedUnit, _value?.Unit ?? Choice?.Unit ?? "");

    /// <summary>
    /// How full the bar is, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Computed against the source's own value and range, before any display conversion.
    /// Converting first would need the range converting too, and a bar that is 40% full in °C
    /// and 55% full in °F would be a genuinely misleading thing to put on a windscreen.
    /// </remarks>
    public double BarFraction
    {
        get
        {
            if (!IsBar || _value is not { IsUsable: true } value || double.IsNaN(value.Value))
            {
                return 0;
            }

            var min = Choice!.Min!.Value;
            var max = Choice.Max!.Value;

            return Math.Clamp((value.Value - min) / (max - min), 0, 1);
        }
    }

    /// <summary>
    /// The card's bottom line: an allocated rate, or where a sensor answered from.
    /// </summary>
    /// <remarks>
    /// Was <c>RateText</c>, and the rename matters. A sensor has no allocated rate — nothing
    /// was asked of the truck and no budget was spent — so reporting one would be inventing a
    /// cost that does not exist. It says <c>TABLET · TRUE</c> or <c>TRUCK</c> instead, which
    /// is the thing actually worth knowing about a value the tablet might be guessing at.
    /// </remarks>
    public string FooterText
    {
        get
        {
            if (IsMissing)
            {
                return "not in a catalog";
            }

            return _value?.Footer ?? "suspended";
        }
    }

    /// <summary>True while this card holds a live value.</summary>
    public bool IsActive => _value is not null;

    /// <summary>
    /// Start supplying this card.
    /// </summary>
    /// <remarks>
    /// Idempotent, because the dashboard re-runs activation on every page change and every
    /// re-pack, and a card that re-declared each time would churn the polling plan for no
    /// reason.
    /// </remarks>
    public void Activate()
    {
        if (_disposed || _value is not null || IsMissing)
        {
            return;
        }

        _value = _values.Create(Spec, Choice);

        if (_value is not null)
        {
            _value.Changed += RaiseValueProperties;
        }

        RaiseValueProperties();
    }

    /// <summary>Stop. A signal withdraws its declaration; a sensor stops polling.</summary>
    public void Deactivate()
    {
        if (_value is null)
        {
            return;
        }

        _value.Changed -= RaiseValueProperties;
        _value.Dispose();
        _value = null;

        RaiseValueProperties();
    }

    /// <summary>
    /// Adopt an edited spec.
    /// </summary>
    /// <remarks>
    /// The value is torn down and rebuilt whenever anything about where the number comes from
    /// changed — the source, the id, the rate, the priority — and left alone when only
    /// presentation did, so switching a card to Fahrenheit does not cost a re-plan.
    /// </remarks>
    public void Apply(CardSpec spec, ValueChoice? choice)
    {
        var wasActive = IsActive;

        var rebind = !string.Equals(spec.SignalId, Spec.SignalId, StringComparison.Ordinal)
            || spec.ParsedSource != Spec.ParsedSource
            || spec.RateHz != Spec.RateHz
            || spec.ParsedPriority != Spec.ParsedPriority
            || !string.Equals(spec.Format, Spec.Format, StringComparison.Ordinal);

        if (rebind)
        {
            Deactivate();
        }

        Spec = spec;
        Choice = choice;

        if (rebind && wasActive)
        {
            Activate();
        }

        OnPropertyChanged(nameof(Spec));
        OnPropertyChanged(nameof(Choice));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(IsBar));
        OnPropertyChanged(nameof(IsMissing));
        RaiseValueProperties();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        Deactivate();
    }

    private void RaiseValueProperties()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(BarFraction));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(IsActive));
    }
}
