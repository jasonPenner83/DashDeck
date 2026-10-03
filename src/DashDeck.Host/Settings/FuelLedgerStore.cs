using DashDeck.Core.Fuel;

namespace DashDeck.Host.Settings;

/// <summary>
/// The fuel ledger on disk — <c>%LOCALAPPDATA%\DashDeck\fuel.json</c> (ADR-0041): the
/// calibration factor and what has been used since the last fill-up.
/// </summary>
/// <remarks>
/// A deploy never touches it, so a calibration learned over months survives every update. Only
/// what the real truck counted, and fill-ups entered by hand, are ever written.
/// </remarks>
public sealed class FuelLedgerStore
{
    private readonly string _path;

    /// <summary>The standard store, in the user's profile.</summary>
    public FuelLedgerStore()
        : this(JsonFile.InLocalAppData("fuel.json"))
    {
    }

    /// <summary>A store at an explicit path. For tests, which must not touch the real profile.</summary>
    public FuelLedgerStore(string path)
    {
        _path = path;
        Ledger = JsonFile.Load<FuelLedger>(path, out var error) ?? FuelLedger.Fresh;
        LastError = error;
    }

    /// <summary>What was saved last, or a fresh ledger.</summary>
    public FuelLedger Ledger { get; private set; }

    /// <summary>Why the last load or save failed, if it did.</summary>
    public string? LastError { get; private set; }

    public void Save(FuelLedger ledger)
    {
        Ledger = ledger;
        JsonFile.Save(_path, ledger, out var error);
        LastError = error;
    }
}
