using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Core.Fuel;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The FUEL block of Settings ▸ Vehicle (ADR-0041): where fuel flow comes from, how well the
/// estimate is calibrated, what has been used since the last fill-up, and the fill-up entry that
/// calibrates it.
/// </summary>
public sealed partial class FuelCalibrationViewModel : ObservableObject
{
    private readonly FuelModel? _fuel;

    public FuelCalibrationViewModel(FuelModel? fuel)
    {
        _fuel = fuel;

        if (fuel is not null)
        {
            fuel.LedgerChanged += (_, _) => Refresh();
        }

        Refresh();
    }

    /// <summary>False when the vehicle stack has no fuel model — the block says so instead.</summary>
    public bool IsAvailable => _fuel is not null;

    /// <summary>Where the flow comes from now, in words.</summary>
    [ObservableProperty]
    private string _sourceText = "";

    /// <summary>The calibration, in words.</summary>
    [ObservableProperty]
    private string _calibrationText = "";

    /// <summary>Used and driven since the last fill-up.</summary>
    [ObservableProperty]
    private string _sinceFillText = "";

    /// <summary>What the pump put in, typed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordFillCommand))]
    private string _fillLitres = "";

    /// <summary>What the last button did.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>True after one tap of RESET: the second tap does it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResetCaption))]
    private bool _isResetArmed;

    public string ResetCaption => IsResetArmed ? "TAP AGAIN TO RESET" : "RESET CALIBRATION";

    private bool CanRecordFill() => _fuel is not null && TryLitres(out _);

    [RelayCommand(CanExecute = nameof(CanRecordFill))]
    private void RecordFill()
    {
        if (_fuel is null || !TryLitres(out var litres))
        {
            return;
        }

        Status = _fuel.RecordFill(litres);
        FillLitres = "";
        Refresh();
    }

    [RelayCommand]
    private void Reset()
    {
        if (_fuel is null)
        {
            return;
        }

        if (!IsResetArmed)
        {
            IsResetArmed = true;
            return;
        }

        IsResetArmed = false;
        _fuel.Reset();
        Status = "Calibration and counts cleared. Fill to full and enter it to start again.";
        Refresh();
    }

    private bool TryLitres(out double litres) =>
        double.TryParse(FillLitres.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out litres)
        && litres is > 0 and < 500;

    /// <summary>Read the model again — on the clock beat while the section is open, and after a fill-up.</summary>
    public void Refresh()
    {
        if (_fuel is null)
        {
            SourceText = "Fuel counting is not running.";
            CalibrationText = "";
            SinceFillText = "";
            return;
        }

        var ledger = _fuel.Ledger;
        var source = _fuel.CurrentSource;

        SourceText = source switch
        {
            FuelFlowSource.Truck => "FUEL FLOW: FROM THE TRUCK (its own fuel rate)",
            FuelFlowSource.Calibrated => "FUEL FLOW: ESTIMATED — speed-density, calibrated",
            _ => "FUEL FLOW: ESTIMATED — speed-density, NOT YET CALIBRATED",
        };

        CalibrationText = ledger.IsCalibrated
            ? string.Create(CultureInfo.InvariantCulture, $"Factor {ledger.Factor:0.00}, learned from {ledger.CalibratingFills} fill-up{(ledger.CalibratingFills == 1 ? "" : "s")} ({ledger.CalibratingLitres:0} L).")
            : ledger.HasBaseline
                ? "Not calibrated yet. The next full-to-full fill-up calibrates it."
                : "Not calibrated yet. Fill to full and enter it below to start the count.";

        SinceFillText = ledger.HasBaseline
            ? string.Create(CultureInfo.InvariantCulture, $"Since the last fill-up: {ledger.LitresSinceFill:0.0} L over {ledger.KmSinceFill:0} km (estimate before calibration {ledger.EstimatedSinceFill:0.0} L).")
            : "No fill-up recorded yet.";

        RecordFillCommand.NotifyCanExecuteChanged();
    }
}
