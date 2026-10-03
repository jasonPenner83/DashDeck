using System.IO;
using DashDeck.Abstractions;
using DashDeck.Core.Arbitration;
using DashDeck.Core.Bus;
using DashDeck.Core.Catalog;
using DashDeck.Core.Fuel;
using DashDeck.Host.Settings;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>Settings ▸ Vehicle ▸ FUEL (ADR-0041): the ledger on disk, and fill-ups entered by hand.</summary>
public sealed class FuelCalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dashdeck-fuel-{Guid.NewGuid():N}");

    public FuelCalibrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string CatalogPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", "signals.obd2-standard.json");

    private static FuelModel Model(FuelLedgerStore store)
    {
        var catalog = FuelModel.AddTo(SignalCatalog.FromFile(CatalogPath()));
        var bus = new VehicleStateBus(catalog, new RequestArbiter(catalog) { BudgetHz = 19 }, SystemClock.Instance);
        var model = new FuelModel(bus, SystemClock.Instance, () => new VehicleProfile { EngineDisplacementLitres = 2.7 }, store.Ledger, store.Save);
        model.Start();
        return model;
    }

    [Fact]
    public void The_ledger_survives_a_restart()
    {
        var path = Path.Combine(_dir, "fuel.json");
        var store = new FuelLedgerStore(path);
        store.Save(new FuelLedger { Factor = 1.18, CalibratingFills = 2, CalibratingLitres = 120, HasBaseline = true, KmSinceFill = 42 });

        var again = new FuelLedgerStore(path);

        Assert.Null(again.LastError);
        Assert.Equal(1.18, again.Ledger.Factor);
        Assert.Equal(2, again.Ledger.CalibratingFills);
        Assert.Equal(42, again.Ledger.KmSinceFill);
        Assert.Contains("\"factor\"", File.ReadAllText(path));
    }

    [Fact]
    public void A_missing_file_is_a_fresh_ledger()
    {
        var store = new FuelLedgerStore(Path.Combine(_dir, "none.json"));

        Assert.Equal(FuelLedger.Fresh, store.Ledger);
        Assert.False(store.Ledger.IsCalibrated);
    }

    [Fact]
    public void Filled_up_starts_the_count_and_is_saved()
    {
        var store = new FuelLedgerStore(Path.Combine(_dir, "fuel.json"));
        using var model = Model(store);
        var vm = new FuelCalibrationViewModel(model);

        Assert.Contains("NOT YET CALIBRATED", vm.SourceText);
        Assert.False(vm.RecordFillCommand.CanExecute(null));

        vm.FillLitres = "62,5";
        Assert.True(vm.RecordFillCommand.CanExecute(null));
        vm.RecordFillCommand.Execute(null);

        Assert.Contains("First fill-up", vm.Status);
        Assert.Equal("", vm.FillLitres);
        Assert.True(new FuelLedgerStore(Path.Combine(_dir, "fuel.json")).Ledger.HasBaseline);
        Assert.Contains("0.0 L over 0 km", vm.SinceFillText);
    }

    [Fact]
    public void Reset_takes_two_taps()
    {
        var store = new FuelLedgerStore(Path.Combine(_dir, "fuel.json"));
        store.Save(new FuelLedger { Factor = 1.2, CalibratingFills = 1, CalibratingLitres = 60, HasBaseline = true });
        using var model = Model(store);
        var vm = new FuelCalibrationViewModel(model);

        vm.ResetCommand.Execute(null);
        Assert.True(model.Ledger.IsCalibrated);
        Assert.Equal("TAP AGAIN TO RESET", vm.ResetCaption);

        vm.ResetCommand.Execute(null);
        Assert.False(model.Ledger.IsCalibrated);
        Assert.False(new FuelLedgerStore(Path.Combine(_dir, "fuel.json")).Ledger.IsCalibrated);
    }
}
