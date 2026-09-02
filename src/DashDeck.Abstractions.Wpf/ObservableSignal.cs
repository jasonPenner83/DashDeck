using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DashDeck.Abstractions.Wpf;

/// <summary>
/// One vehicle signal, bindable. Declares the demand, observes the readings, marshals them
/// onto the UI thread and renders quality — so no component has to do any of it.
/// </summary>
/// <remarks>
/// This type exists because two mistakes are otherwise near-certain, and both are worse on
/// a dash than they sound:
/// <list type="bullet">
///   <item>
///     <b>Thread marshalling.</b> All vehicle I/O runs on its own worker. Raising
///     <see cref="INotifyPropertyChanged"/> from that thread throws or, worse, silently
///     drops updates. Every raise here goes through the dispatcher captured at
///     construction.
///   </item>
///   <item>
///     <b>Silent binding failure.</b> A WPF binding that breaks logs to the debug output
///     and otherwise renders nothing — indistinguishable, on glass, from a working display
///     showing an unchanging number. Binding to <see cref="Text"/> means an absent value is
///     always visibly absent, because it renders as <see cref="Placeholder"/> rather than
///     as a stale digit or a zero.
///   </item>
/// </list>
/// <para>
/// Construct on the UI thread. Dispose to withdraw the demand — the arbiter reclaims the
/// budget for whatever is still on screen.
/// </para>
/// </remarks>
public sealed class ObservableSignal : INotifyPropertyChanged, IDisposable
{
    /// <summary>What is rendered when there is no trustworthy value. Never a zero.</summary>
    public const string Placeholder = "——";

    private readonly Dispatcher _dispatcher;
    private readonly ISignalSubscription _demand;
    private readonly IDisposable _observer;
    private readonly string _format;

    private SignalValue _value;
    private double _effectiveRateHz;
    private bool _disposed;

    /// <summary>
    /// Declare a signal and start observing it.
    /// </summary>
    /// <param name="signals">The component's signal access.</param>
    /// <param name="signalId">Catalog id, e.g. <c>vehicle.speed</c>.</param>
    /// <param name="priority">What survives when the adapter's budget is oversubscribed.</param>
    /// <param name="rateHz">
    /// The rate to ask for. Ask honestly — the whole app shares one serialised link, and
    /// asking for 30 Hz does not produce 30 Hz, it degrades everyone.
    /// </param>
    /// <param name="format">Numeric format for <see cref="Text"/>. Defaults to one decimal.</param>
    public ObservableSignal(
        IVehicleSignals signals,
        string signalId,
        SignalPriority priority = SignalPriority.Normal,
        double rateHz = 1.0,
        string format = "0.#")
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);

        _dispatcher = Dispatcher.CurrentDispatcher;
        _format = format;

        _value = signals.Current(signalId);
        _demand = signals.Require(signalId, priority, rateHz);
        _effectiveRateHz = _demand.EffectiveRateHz;

        _demand.EffectiveRateChanged += OnEffectiveRateChanged;
        _observer = signals.Subscribe(signalId, OnValue);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Catalog id of the signal being observed.</summary>
    public string SignalId => _demand.SignalId;

    /// <summary>The latest reading, whatever its quality.</summary>
    public SignalValue Current => _value;

    /// <summary>The decoded number. May be <see cref="double.NaN"/> — bind to <see cref="Text"/> instead.</summary>
    public double Value => _value.Value;

    /// <summary>Unit symbol from the catalog, e.g. <c>km/h</c>. Read it; never assume it.</summary>
    public string Unit => _value.Unit;

    /// <summary>How far this reading can be trusted right now.</summary>
    public SignalQuality Quality => _value.Quality;

    /// <summary>True when the reading is recent enough to show as a number.</summary>
    public bool IsUsable => _value.IsUsable;

    /// <summary>
    /// The value formatted for display, or <see cref="Placeholder"/> when there is nothing
    /// trustworthy to show. This is what a widget should bind to.
    /// </summary>
    public string Text => _value.IsUsable && !double.IsNaN(_value.Value)
        ? _value.Value.ToString(_format, CultureInfo.CurrentCulture)
        : Placeholder;

    /// <summary>
    /// What the arbiter is actually delivering. Lower than requested means the signal was
    /// degraded under contention — worth showing rather than looking frozen.
    /// </summary>
    public double EffectiveRateHz => _effectiveRateHz;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _demand.EffectiveRateChanged -= OnEffectiveRateChanged;
        _observer.Dispose();
        _demand.Dispose();
    }

    private void OnValue(SignalValue value) => OnUiThread(() =>
    {
        _value = value;
        Raise(nameof(Current));
        Raise(nameof(Value));
        Raise(nameof(Unit));
        Raise(nameof(Quality));
        Raise(nameof(IsUsable));
        Raise(nameof(Text));
    });

    private void OnEffectiveRateChanged(double rateHz) => OnUiThread(() =>
    {
        _effectiveRateHz = rateHz;
        Raise(nameof(EffectiveRateHz));
    });

    /// <summary>
    /// Run on the dispatcher thread. Readings arrive on the vehicle worker, so this is the
    /// single place that boundary is crossed.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        // Fire-and-forget by design: a reading that arrives while the dispatcher is
        // shutting down is not worth propagating an exception for.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
