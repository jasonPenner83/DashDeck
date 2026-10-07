using DashDeck.Abstractions;
using DashDeck.Core.Actions;
using DashDeck.Core.Diagnostics;
using DashDeck.Core.Discovery;
using DashDeck.Core.Catalog;
using DashDeck.Vehicle;

namespace DashDeck.Host.Warnings;

/// <summary>
/// What the warning window and Settings ▸ Diagnostics ask of the truck: read the codes, and clear
/// them through the gated choke point (ADR-0055). An interface, so the screens are tested without one.
/// </summary>
public interface IVehicleDiagnostics
{
    /// <summary>What a code is about.</summary>
    TroubleCodeDescriptions Descriptions { get; }

    /// <summary>Gate 2: the master switch, now.</summary>
    bool ClearAllowed { get; }

    /// <summary>Where every clearing attempt is written.</summary>
    string ActionLogPath { get; }

    Task<TroubleCodeReport> ReadCodesAsync(CancellationToken ct);

    Task<ActionInterlock> CheckClearAsync(CancellationToken ct);

    Task<ActionResult> ClearCodesAsync(ActionConfirmation confirmation, string shown, CancellationToken ct);
}

/// <summary>The real one: the running vehicle stack, straight to the adapter outside the polling plan.</summary>
public sealed class VehicleDiagnostics : IVehicleDiagnostics
{
    private readonly Func<PidRequest, CancellationToken, Task<PidResponse>> _ask;
    private readonly Func<ObdReference> _reference;
    private readonly IClock _clock;
    private readonly VehicleActions _actions;

    public VehicleDiagnostics(
        Func<PidRequest, CancellationToken, Task<PidResponse>> ask,
        Func<SignalCatalog> catalog,
        Func<ObdReference> reference,
        Func<bool> clearAllowed,
        TroubleCodeDescriptions descriptions,
        string actionLogPath,
        IClock clock)
    {
        _ask = ask;
        _reference = reference;
        _clock = clock;
        Descriptions = descriptions;
        ActionLogPath = actionLogPath;
        _actions = new VehicleActions(ask, catalog, clearAllowed, new ActionLogFile(actionLogPath), clock);
    }

    public TroubleCodeDescriptions Descriptions { get; }

    public bool ClearAllowed => _actions.IsEnabled;

    public string ActionLogPath { get; }

    public Task<TroubleCodeReport> ReadCodesAsync(CancellationToken ct) =>
        TroubleCodeReader.ReadAsync(_ask, _reference().Modules, _clock, ct);

    public Task<ActionInterlock> CheckClearAsync(CancellationToken ct) => _actions.CheckClearCodesAsync(ct);

    public Task<ActionResult> ClearCodesAsync(ActionConfirmation confirmation, string shown, CancellationToken ct) =>
        _actions.ClearDiagnosticCodesAsync(confirmation, shown, ct);
}
