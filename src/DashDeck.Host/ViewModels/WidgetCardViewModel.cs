using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;
using DashDeck.Host.Dash;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// One card on the dash: a spec, the signal it is bound to, and what it draws.
/// </summary>
/// <remarks>
/// Two things distinguish this from the fixed widgets it replaces.
/// <para>
/// <b>It can be switched off.</b> <see cref="Activate"/> declares the signal and
/// <see cref="Deactivate"/> withdraws it, and the dashboard only activates the page you are
/// looking at. Every card is demand on one serialised link whose real ceiling is still
/// unmeasured (Q12), so thirty cards all declaring at once would degrade the six actually
/// in front of you. A card you cannot see asks for nothing.
/// </para>
/// <para>
/// <b>It formats for itself.</b> It cannot simply bind through to
/// <see cref="ObservableSignal.Text"/>, because the display unit is a per-card choice and
/// the conversion belongs at the very edge — see <see cref="DisplayUnits"/>. So it wraps the
/// signal and re-raises, which also gives the missing-signal case somewhere honest to live.
/// </para>
/// </remarks>
public sealed partial class WidgetCardViewModel : ObservableObject, IDashSlot, IDisposable
{
    private readonly IVehicleSignals _signals;
    private ObservableSignal? _signal;
    private bool _disposed;

    public WidgetCardViewModel(IVehicleSignals signals, CardSpec spec, SignalChoice? choice)
    {
        _signals = signals;
        Spec = spec;
        Choice = choice;
    }

    /// <summary>What this card is, as stored.</summary>
    public CardSpec Spec { get; private set; }

    /// <summary>Catalog metadata for the bound signal, or null if the catalog has no such signal.</summary>
    public SignalChoice? Choice { get; private set; }

    /// <summary>
    /// True when the card names a signal the loaded catalog does not define.
    /// </summary>
    /// <remarks>
    /// Kept and rendered as broken rather than dropped. ADR-0012 asks widget validation to
    /// fail loudly, and quietly deleting a card because a catalog file moved is the loudest
    /// possible failure to notice and the worst possible one to recover from. It also cannot
    /// simply be activated: the arbiter throws on an unknown id, by design.
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

    /// <summary>How much this reading can be trusted. A missing signal is Unavailable, honestly.</summary>
    public SignalQuality Quality => _signal?.Quality ?? SignalQuality.Unavailable;

    /// <summary>The value, converted and formatted, or the placeholder.</summary>
    public string Text
    {
        get
        {
            if (IsMissing || _signal is not { IsUsable: true } signal || double.IsNaN(signal.Value))
            {
                return ObservableSignal.Placeholder;
            }

            return DisplayUnits.Format(signal.Value, signal.Unit, Spec.ParsedUnit, Spec.Format);
        }
    }

    /// <summary>
    /// The unit symbol as displayed, which is not always the one the truck sent.
    /// </summary>
    /// <remarks>
    /// Blank for a missing signal rather than an explanation. The unit slot is sized for
    /// "km/h" and a sentence in it simply ran off the side of the card; the quality badge
    /// already says UNAVAIL and <see cref="RateText"/> says which signal is gone.
    /// </remarks>
    public string UnitText => IsMissing
        ? string.Empty
        : DisplayUnits.Symbol(Spec.ParsedUnit, _signal?.Unit ?? Choice?.Unit ?? "");

    /// <summary>
    /// How full the bar is, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Computed against the <em>catalog</em> value and the catalog range, before any display
    /// conversion. Converting first would need the range converting too, and a bar that is
    /// 40% full in °C and 55% full in °F would be a genuinely misleading thing to put on a
    /// windscreen.
    /// </remarks>
    public double BarFraction
    {
        get
        {
            if (!IsBar || _signal is not { IsUsable: true } signal || double.IsNaN(signal.Value))
            {
                return 0;
            }

            var min = Choice!.Min!.Value;
            var max = Choice.Max!.Value;

            return Math.Clamp((signal.Value - min) / (max - min), 0, 1);
        }
    }

    /// <summary>What the arbiter is actually delivering, for the card footer.</summary>
    public string RateText
    {
        get
        {
            if (IsMissing)
            {
                // Names the signal, because "not in the catalog" leaves you guessing which
                // of a card's settings is the broken one.
                return $"no signal '{Spec.SignalId}'";
            }

            return _signal is null
                ? "suspended"
                : string.Create(CultureInfo.CurrentCulture, $"{_signal.EffectiveRateHz:0.##} Hz allocated");
        }
    }

    /// <summary>True while this card holds a live declaration.</summary>
    public bool IsActive => _signal is not null;

    /// <summary>
    /// Declare the signal and start observing it.
    /// </summary>
    /// <remarks>
    /// Idempotent, because the dashboard re-runs activation on every page change and every
    /// re-pack, and a card that re-declared each time would churn the polling plan for no
    /// reason.
    /// </remarks>
    public void Activate()
    {
        if (_disposed || _signal is not null || IsMissing)
        {
            return;
        }

        _signal = new ObservableSignal(
            _signals,
            Spec.SignalId,
            Spec.ParsedPriority,
            Spec.RateHz,
            Spec.Format);

        _signal.PropertyChanged += OnSignalChanged;
        RaiseValueProperties();
    }

    /// <summary>Withdraw the declaration. The arbiter reclaims the budget for what is on screen.</summary>
    public void Deactivate()
    {
        if (_signal is null)
        {
            return;
        }

        _signal.PropertyChanged -= OnSignalChanged;
        _signal.Dispose();
        _signal = null;

        RaiseValueProperties();
    }

    /// <summary>
    /// Adopt an edited spec.
    /// </summary>
    /// <remarks>
    /// The declaration is torn down and rebuilt whenever anything the arbiter cares about
    /// changed — signal, rate, priority — and left alone when only presentation did, so
    /// switching a card to Fahrenheit does not cost a re-plan.
    /// </remarks>
    public void Apply(CardSpec spec, SignalChoice? choice)
    {
        var wasActive = IsActive;

        var rebind = !string.Equals(spec.SignalId, Spec.SignalId, StringComparison.Ordinal)
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

    private void OnSignalChanged(object? sender, PropertyChangedEventArgs e) => RaiseValueProperties();

    private void RaiseValueProperties()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(BarFraction));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(IsActive));
    }
}
