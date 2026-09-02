using DashDeck.Abstractions;
using DashDeck.Abstractions.Wpf;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// One widget in a band on the home screen.
/// </summary>
/// <remarks>
/// Stands in for what a real component will contribute through
/// <see cref="IDashComponentView.CreateWidget"/>. Until the component host exists these are
/// built by the shell, but they go through the same <see cref="ObservableSignal"/> a
/// component would use — so if that helper is awkward, we find out here rather than in P1.
/// </remarks>
public sealed class SignalWidgetViewModel : IDisposable
{
    public SignalWidgetViewModel(
        IVehicleSignals signals,
        string label,
        string signalId,
        SignalPriority priority,
        double rateHz,
        string format = "0.#")
    {
        Label = label;
        Signal = new ObservableSignal(signals, signalId, priority, rateHz, format);
    }

    /// <summary>Short uppercase caption, e.g. <c>SPEED</c>.</summary>
    public string Label { get; }

    /// <summary>The live value, already marshalled and quality-aware.</summary>
    public ObservableSignal Signal { get; }

    /// <summary>Withdraws the signal demand so the arbiter can reclaim the budget.</summary>
    public void Dispose() => Signal.Dispose();
}
