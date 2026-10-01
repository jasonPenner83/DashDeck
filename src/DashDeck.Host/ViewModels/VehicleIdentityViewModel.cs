using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Identity;
using DashDeck.Host.Identity;
using DashDeck.Host.Settings;
using DashDeck.Vehicle;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Which vehicle this is, in Settings ▸ Vehicle: its VIN, what the VIN decodes to, and the
/// signal pack that picks (ADR-0033).
/// </summary>
/// <remarks>
/// <b>Truck first, typed as the fallback</b> (ADR-0016): READ FROM TRUCK asks the vehicle for
/// its own VIN, and the box takes one typed or pasted when no adapter is in reach. LOOK UP sends
/// it to NHTSA once and caches the answer; every decoded field can then be corrected by hand,
/// because a decoder is sometimes wrong and often silent. Like every fact that shapes the
/// pipeline, it applies at the next launch — the profile is a snapshot (ADR-0029) and the pack
/// is chosen when the catalog is built.
/// </remarks>
public sealed partial class VehicleIdentityViewModel : ObservableObject
{
    private readonly VehicleIdentityStore _store;
    private readonly IVinDecoder _decoder;
    private readonly Func<PidRequest, CancellationToken, Task<PidResponse>> _probe;
    private readonly IClock _clock;
    private readonly bool _isSimulated;
    private readonly IReadOnlyList<VehiclePack> _availablePacks;
    private readonly IReadOnlyList<VehiclePack> _activePacks;
    private readonly Action? _restart;
    private readonly VehicleIdentity _atLaunch;

    /// <summary>The decoder's own facts that have no field: the VIN it decoded, its source and time.</summary>
    private VehicleIdentity _decoded;

    /// <summary>True while fields are being filled in code, so that is not mistaken for an edit.</summary>
    private bool _filling;

    public VehicleIdentityViewModel(
        VehicleIdentityStore store,
        IVinDecoder decoder,
        Func<PidRequest, CancellationToken, Task<PidResponse>> probe,
        IClock clock,
        bool isSimulated,
        IReadOnlyList<VehiclePack> availablePacks,
        IReadOnlyList<VehiclePack> activePacks,
        string? packProblem,
        Action? restart = null)
    {
        _store = store;
        _decoder = decoder;
        _probe = probe;
        _clock = clock;
        _isSimulated = isSimulated;
        _availablePacks = availablePacks;
        _activePacks = activePacks;
        _restart = restart;
        PackProblem = packProblem;

        _atLaunch = store.Identity;
        _decoded = store.Identity;
        _vinText = store.Identity.Vin ?? "";
        Fill(store.Identity);
    }

    // ── The VIN ───────────────────────────────────────────────────────────────

    /// <summary>The VIN as typed, read or pasted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VinMessage), nameof(VinIsSuspect))]
    [NotifyCanExecuteChangedFor(nameof(LookUpCommand))]
    private string _vinText;

    private string Normalized => Vin.Normalize(VinText);

    /// <summary>What is wrong with the VIN as typed, or that it looks right.</summary>
    public string VinMessage
    {
        get
        {
            var vin = Normalized;

            if (vin.Length == 0)
            {
                return "Read it from the truck, or type the 17 characters from the windscreen or the driver's door sticker.";
            }

            if (!Vin.IsWellFormed(vin))
            {
                return $"A VIN is 17 letters and digits, never I, O or Q — {vin.Length} so far.";
            }

            return Vin.HasValidCheckDigit(vin)
                ? "Looks right."
                : $"Position 9 should be {Vin.ExpectedCheckDigit(vin)} for a North American VIN — check for a typo. VINs from elsewhere may not use it.";
        }
    }

    /// <summary>True while the VIN is malformed or fails its check digit, so the line reads as a warning.</summary>
    public bool VinIsSuspect => Normalized.Length > 0 && (!Vin.IsWellFormed(Normalized) || !Vin.HasValidCheckDigit(Normalized));

    /// <summary>What READ or LOOK UP last did.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>True when <see cref="Status"/> is a failure.</summary>
    [ObservableProperty]
    private bool _statusIsProblem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadFromTruckCommand), nameof(LookUpCommand))]
    private bool _isBusy;

    private bool CanRead() => !IsBusy;

    /// <summary>Ask the truck for its own VIN — mode 09, PID 02, a read.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadFromTruckAsync()
    {
        IsBusy = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = await VinReader.ReadAsync(_probe, timeout.Token);

            if (read.Vin is { } vin)
            {
                VinText = vin;
                Say(_isSimulated
                    ? "Read from the synthetic truck — the simulator's made-up VIN, not yours. Plug in the adapter to read the real one."
                    : "Read from the truck. LOOK UP to decode it.", problem: false);
            }
            else
            {
                Say($"Couldn't read the VIN: {read.Problem}. You can type it instead.", problem: true);
            }
        }
        catch (OperationCanceledException)
        {
            Say("The truck didn't answer in time. You can type the VIN instead.", problem: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLookUp() => !IsBusy && Vin.IsWellFormed(Normalized);

    /// <summary>Decode the VIN with NHTSA, fill the fields, and cache the answer.</summary>
    [RelayCommand(CanExecute = nameof(CanLookUp))]
    private async Task LookUpAsync()
    {
        IsBusy = true;
        var vin = Normalized;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _decoder.DecodeAsync(vin, _clock.UtcNow, timeout.Token);

            if (result.Identity is { } identity)
            {
                _decoded = identity;
                Fill(identity);
                Save(identity);

                Say(result.Problem is { } warning
                    ? $"Decoded, partly: {warning}. Fill in anything missing below."
                    : "Decoded. Correct anything below that is wrong.", problem: false);
            }
            else
            {
                Say($"Couldn't decode it: {result.Problem}. Nothing was changed.", problem: true);
            }
        }
        catch (OperationCanceledException)
        {
            Say("NHTSA didn't answer in time. Nothing was changed.", problem: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Delete the VIN and everything decoded from it from this tablet.</summary>
    [RelayCommand]
    private void Forget()
    {
        _store.Forget();
        _decoded = VehicleIdentity.Unknown;
        VinText = "";
        Fill(VehicleIdentity.Unknown);
        Say(_store.LastError is { } error ? $"Couldn't forget it: {error}" : "Forgotten. Nothing about the vehicle is stored on this tablet.", _store.LastError is not null);
        Changed();
    }

    // ── What it is, editable ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _yearText = "";

    [ObservableProperty]
    private string _make = "";

    [ObservableProperty]
    private string _model = "";

    [ObservableProperty]
    private string _trim = "";

    [ObservableProperty]
    private string _displacementText = "";

    [ObservableProperty]
    private string _cylindersText = "";

    [ObservableProperty]
    private string _fuelType = "";

    /// <summary>Turbocharged: yes, no or not known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TurboLabel))]
    private bool? _turbocharged;

    /// <summary>The turbo toggle's caption.</summary>
    public string TurboLabel => Turbocharged switch
    {
        true => "TURBO — YES",
        false => "TURBO — NO",
        null => "TURBO — NOT KNOWN",
    };

    /// <summary>Yes, no, not known, round again.</summary>
    [RelayCommand]
    private void CycleTurbo() => Turbocharged = Turbocharged switch
    {
        null => true,
        true => false,
        false => null,
    };

    /// <summary>Why the fields as typed cannot be saved, or empty.</summary>
    [ObservableProperty]
    private string _fieldProblem = "";

    partial void OnYearTextChanged(string value) => Edited();

    partial void OnMakeChanged(string value) => Edited();

    partial void OnModelChanged(string value) => Edited();

    partial void OnTrimChanged(string value) => Edited();

    partial void OnDisplacementTextChanged(string value) => Edited();

    partial void OnCylindersTextChanged(string value) => Edited();

    partial void OnFuelTypeChanged(string value) => Edited();

    partial void OnTurbochargedChanged(bool? value) => Edited();

    private void Edited()
    {
        if (_filling)
        {
            return;
        }

        if (Current() is { } identity)
        {
            FieldProblem = "";
            Save(identity);
        }
    }

    /// <summary>
    /// The vehicle the fields describe, or null when one does not parse. A field changed by hand
    /// says so in the source, so nobody mistakes a correction for what the decoder said.
    /// </summary>
    internal VehicleIdentity? Current()
    {
        int? year = null;
        double? litres = null;
        int? cylinders = null;

        if (!string.IsNullOrWhiteSpace(YearText))
        {
            if (!int.TryParse(YearText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) || y is < 1981 or > 2100)
            {
                FieldProblem = "Year must be a model year, like 2019.";
                return null;
            }

            year = y;
        }

        if (!string.IsNullOrWhiteSpace(DisplacementText))
        {
            if (!double.TryParse(DisplacementText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var l) || l is <= 0 or > 20)
            {
                FieldProblem = "Engine size is in litres, like 2.7.";
                return null;
            }

            litres = l;
        }

        if (!string.IsNullOrWhiteSpace(CylindersText))
        {
            if (!int.TryParse(CylindersText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) || c is <= 0 or > 16)
            {
                FieldProblem = "Cylinders must be a whole number, like 6.";
                return null;
            }

            cylinders = c;
        }

        var edited = new VehicleIdentity
        {
            Vin = _decoded.Vin,
            ModelYear = year,
            Make = Blank(Make),
            Model = Blank(Model),
            Trim = Blank(Trim),
            DisplacementLitres = litres,
            Cylinders = cylinders,
            Turbocharged = Turbocharged,
            FuelType = Blank(FuelType),
            DecodedUtc = _decoded.DecodedUtc,
            Source = _decoded.Source,
        };

        var matchesDecode = edited with { Source = null, DecodedUtc = null } == _decoded with { Source = null, DecodedUtc = null };

        return edited with
        {
            Source = matchesDecode ? _decoded.Source
                : _decoded.Source is { } source && !source.EndsWith("your corrections", StringComparison.Ordinal)
                    ? $"{source} + your corrections"
                    : _decoded.Source ?? "Entered by hand",
        };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void Fill(VehicleIdentity identity)
    {
        _filling = true;

        try
        {
            YearText = identity.ModelYear?.ToString(CultureInfo.InvariantCulture) ?? "";
            Make = identity.Make ?? "";
            Model = identity.Model ?? "";
            Trim = identity.Trim ?? "";
            DisplacementText = identity.DisplacementLitres?.ToString("0.0#", CultureInfo.InvariantCulture) ?? "";
            CylindersText = identity.Cylinders?.ToString(CultureInfo.InvariantCulture) ?? "";
            FuelType = identity.FuelType ?? "";
            Turbocharged = identity.Turbocharged;
            FieldProblem = "";
        }
        finally
        {
            _filling = false;
        }

        Changed();
    }

    private void Save(VehicleIdentity identity)
    {
        _store.Save(identity);

        if (_store.LastError is { } error)
        {
            Say($"Couldn't save: {error}", problem: true);
        }

        Changed();
    }

    private void Say(string text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(PackText));
        OnPropertyChanged(nameof(RestartNeeded));
    }

    // ── What follows from it ──────────────────────────────────────────────────

    /// <summary>The vehicle in one line, or how to start.</summary>
    public string Summary => _store.Identity.IsKnown
        ? _store.Identity.Describe()
        : "No vehicle set — the standard OBD-II signals apply.";

    /// <summary>Where the facts came from, and when.</summary>
    public string SourceText => _store.Identity switch
    {
        { Source: { } source, DecodedUtc: { } at } => $"From {source}, {at.ToLocalTime():d MMM yyyy}.",
        { Source: { } source } => $"From {source}.",
        _ => "",
    };

    /// <summary>Which vehicle signal pack the stored vehicle picks, and whether it is the running one.</summary>
    public string PackText
    {
        get
        {
            var picked = VehiclePacks.Select(_availablePacks, _store.Identity);

            if (picked.Count == 0)
            {
                return _store.Identity.IsKnown
                    ? "No signal pack for this vehicle yet — the standard OBD-II set applies. Signals you confirm with TEST in Sensors can become one."
                    : "";
            }

            var names = string.Join(", ", picked.Select(p => $"{p.Name} ({p.Signals.Count} signal{(p.Signals.Count == 1 ? "" : "s")})"));
            var running = picked.Select(p => p.Name).SequenceEqual(_activePacks.Select(p => p.Name));

            return running ? $"Signal pack: {names}." : $"Signal pack: {names} — from the next launch.";
        }
    }

    /// <summary>Why a shipped pack was left out, when one was.</summary>
    public string? PackProblem { get; }

    /// <summary>True when what is stored differs from what the dash started with.</summary>
    public bool RestartNeeded => _store.Identity != _atLaunch;

    /// <summary>Whether this build can restart itself.</summary>
    public bool CanRestart => _restart is not null;

    [RelayCommand]
    private void Restart() => _restart?.Invoke();

    /// <summary>Where the decode is cached. Shown, so it can be found or deleted.</summary>
    public string StorePath => _store.Path;
}
