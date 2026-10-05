using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Vehicle;

namespace DashDeck.Host.ViewModels;

/// <summary>One byte-length chip: 1, 2 or 4.</summary>
public sealed partial class ByteLengthOption(int length) : ObservableObject
{
    public int Length { get; } = length;

    public string Label { get; } = length == 1 ? "1 BYTE" : $"{length} BYTES";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One signal definition being written or corrected, in the Sensors section (ADR-0032).
/// </summary>
/// <remarks>
/// Every field is text, parsed as it is typed and checked by the catalog's own
/// <see cref="SignalCatalog.Check"/>, so the refusal arrives while the definition is still
/// being written rather than as a dropped overlay at the next launch.
/// <para>
/// <b>TEST asks the truck before anything is saved.</b> It sends the request once, through the
/// same serialised adapter the plan uses, and shows the raw bytes beside what the decode spec
/// makes of them and whether that lands inside min/max. That is the loop PID discovery is
/// (R2): ask, look at the bytes, adjust the formula, ask again — and the whole reason this is
/// an editor rather than a text file.
/// </para>
/// </remarks>
public sealed partial class SignalEditorViewModel : ObservableObject
{
    private readonly Func<PidRequest, CancellationToken, Task<PidResponse>> _probe;
    private readonly Func<string, bool> _idTaken;
    private readonly Func<ushort, string?> _moduleName;
    private readonly Action<SignalDefinition> _save;
    private readonly Action _cancel;
    private readonly Action<string>? _remove;

    /// <param name="start">The definition to begin from — an existing one, or a suggestion.</param>
    /// <param name="isNew">True when this adds a signal, so its id is still the user's to choose.</param>
    /// <param name="origin">Where an existing definition came from; decides what REMOVE means.</param>
    /// <param name="note">A line about where <paramref name="start"/> came from, or null.</param>
    /// <param name="probe">One request to the adapter, for TEST.</param>
    /// <param name="idTaken">True for an id already in the catalog, for a new signal's check.</param>
    /// <param name="moduleName">A likely name for a module address, or null (ADR-0035).</param>
    /// <param name="save">Write the finished definition.</param>
    /// <param name="cancel">Close without saving.</param>
    /// <param name="remove">Drop the user's definition, when there is one to drop.</param>
    public SignalEditorViewModel(
        SignalDefinition start,
        bool isNew,
        SignalOrigin origin,
        string? note,
        Func<PidRequest, CancellationToken, Task<PidResponse>> probe,
        Func<string, bool> idTaken,
        Func<ushort, string?> moduleName,
        Action<SignalDefinition> save,
        Action cancel,
        Action<string>? remove)
    {
        _probe = probe;
        _idTaken = idTaken;
        _moduleName = moduleName;
        _save = save;
        _cancel = cancel;
        _remove = origin is SignalOrigin.Shipped ? null : remove;

        IsNew = isNew;
        Origin = origin;
        Note = note;

        _id = start.Id;
        _name = start.Name;
        _category = start.Category;
        _isMsCan = start.Bus is CanBus.Ms;
        _modeText = start.Mode.ToString("X2", CultureInfo.InvariantCulture);
        _pidText = start.Pid.ToString(start.Pid <= 0xFF ? "X2" : "X4", CultureInfo.InvariantCulture);
        _moduleText = start.Module ?? "";
        _byteOffsetText = Format(start.Decode.ByteOffset);
        _isSigned = start.Decode.Signed;
        _scaleText = Format(start.Decode.Scale);
        _offsetText = Format(start.Decode.Offset);
        _unit = start.Decode.Unit;
        _rateText = Format(start.DefaultRateHz);
        _minText = start.Min is { } min ? Format(min) : "";
        _maxText = start.Max is { } max ? Format(max) : "";

        ByteLengths = [new(1), new(2), new(4)];
        _byteLength = start.Decode.ByteLength;
        SyncByteLengths();

        // Carried through untouched: the editor has no field for them, and dropping them on save
        // would quietly change a definition the user only meant to rename.
        _kind = start.Kind;
        _stalenessSeconds = start.StalenessSeconds;

        // The mask (a switch's bit, a state's field) and the named states (ADR-0056): the ID
        // matcher found them; TEST and save here must not quietly drop them.
        _mask = start.Decode.Mask;
        _states = start.States;
        _hidden = start.Hidden;
        _unconfirmed = start.Unconfirmed;
    }

    private readonly bool _hidden;
    private readonly bool _unconfirmed;

    /// <summary>The request the last TEST asked, so a passed TEST only confirms what it actually asked.</summary>
    private string? _testedRequest;

    /// <summary>
    /// True when a typed definition (ADR-0051) has had TEST answer exactly what will be saved, so it
    /// is saved confirmed and the card editor offers it.
    /// </summary>
    public bool ConfirmedByTest => _lastPayload is not null && _testedRequest == RequestText;

    private readonly SignalSourceKind _kind;
    private readonly double? _stalenessSeconds;
    private readonly long? _mask;
    private readonly IReadOnlyList<SignalState>? _states;

    /// <summary>True when this adds a signal rather than editing one.</summary>
    public bool IsNew { get; }

    /// <summary>True when the id cannot change — it is what every card and component subscribes to.</summary>
    public bool IsIdLocked => !IsNew;

    /// <summary>Where the definition being edited came from.</summary>
    public SignalOrigin Origin { get; }

    /// <summary>Where the starting point came from, e.g. the J1979 table. Null for none.</summary>
    public string? Note { get; }

    /// <summary>The heading over the form.</summary>
    public string Title => IsNew ? "NEW SIGNAL" : Origin is SignalOrigin.Shipped ? "CORRECT A SHIPPED SIGNAL" : "EDIT YOUR SIGNAL";

    /// <summary>What saving does, said before it happens.</summary>
    public string SaveDetail => Origin is SignalOrigin.Shipped && !IsNew
        ? "Saved as your correction over the shipped definition. The shipped file is never edited."
        : "Saved to your own signal file, beside your other settings.";

    /// <summary>True when there is a user definition to drop.</summary>
    public bool CanRemove => _remove is not null && !IsNew;

    /// <summary>What dropping it does: a correction reverts, an addition goes.</summary>
    public string RemoveCaption => Origin is SignalOrigin.Override ? "REVERT TO SHIPPED" : "REMOVE";

    // ── The fields ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty]
    private string _category;

    /// <summary>False for HS-CAN (pins 6/14), true for MS-CAN (pins 3/11).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusLabel))]
    private bool _isMsCan;

    /// <summary>The bus toggle's caption.</summary>
    public string BusLabel => IsMsCan ? "MS-CAN (3/11)" : "HS-CAN (6/14)";

    [RelayCommand]
    private void ToggleBus() => IsMsCan = !IsMsCan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(RequestText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _modeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(RequestText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _pidText;

    /// <summary>
    /// The module to ask, as a hex address — blank for the broadcast every standard PID uses
    /// (ADR-0035).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(RequestText), nameof(ModuleHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _moduleText;

    /// <summary>What the module field means as typed: the broadcast, a likely name, or a refusal.</summary>
    public string ModuleHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ModuleText))
            {
                return "Blank asks the broadcast (7DF), which the engine computer answers. Ford's own values need the module's address — find it with SCAN FOR MODULES.";
            }

            if (SignalDefinition.ParseModule(ModuleText) is not { } address)
            {
                return "Not a module address: hex, 700–7F7, with the 8s digit clear (the module answers on that +8).";
            }

            return _moduleName(address) is { } name
                ? string.Create(CultureInfo.InvariantCulture, $"Asks {address:X3} and listens on {address + 8:X3} — likely the {name}.")
                : string.Create(CultureInfo.InvariantCulture, $"Asks {address:X3} and listens on {address + 8:X3}.");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(TestDecoded))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _byteOffsetText;

    /// <summary>The chips for 1, 2 and 4 bytes.</summary>
    public IReadOnlyList<ByteLengthOption> ByteLengths { get; }

    private int _byteLength;

    [RelayCommand]
    private void SetByteLength(ByteLengthOption? option)
    {
        if (option is null)
        {
            return;
        }

        _byteLength = option.Length;
        SyncByteLengths();
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(TestDecoded));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void SyncByteLengths()
    {
        foreach (var option in ByteLengths)
        {
            option.IsSelected = option.Length == _byteLength;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignedLabel), nameof(TestDecoded))]
    private bool _isSigned;

    /// <summary>The signedness toggle's caption.</summary>
    public string SignedLabel => IsSigned ? "SIGNED" : "UNSIGNED";

    [RelayCommand]
    private void ToggleSigned() => IsSigned = !IsSigned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(TestDecoded))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _scaleText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(TestDecoded))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _offsetText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestDecoded))]
    private string _unit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _rateText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(TestDecoded))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _minText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problems), nameof(HasProblems), nameof(Message), nameof(TestDecoded))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _maxText;

    // ── Building and checking ─────────────────────────────────────────────────

    /// <summary>
    /// The definition the fields describe, or null with the reasons it cannot be built.
    /// </summary>
    public SignalDefinition? TryBuild(out IReadOnlyList<string> problems)
    {
        var list = new List<string>();

        if (!TryHex(ModeText, 0xFF, out var mode) || mode == 0)
        {
            list.Add("mode must be hex, 01–FF");
        }

        if (!TryHex(PidText, 0xFFFF, out var pid))
        {
            list.Add("PID must be hex, up to four digits");
        }

        var byteOffset = ParseInt(ByteOffsetText, "byte offset", list);
        var scale = ParseNumber(ScaleText, "scale", list);
        var offset = ParseNumber(OffsetText, "offset", list);
        var rate = ParseNumber(RateText, "rate", list);
        var min = ParseOptional(MinText, "min", list);
        var max = ParseOptional(MaxText, "max", list);

        if (IsNew && !string.IsNullOrWhiteSpace(Id) && _idTaken(Id.Trim()))
        {
            list.Add($"'{Id.Trim()}' already exists — edit that one instead");
        }

        if (list.Count > 0)
        {
            problems = list;
            return null;
        }

        var definition = new SignalDefinition
        {
            Id = (Id ?? "").Trim(),
            Name = (Name ?? "").Trim(),
            Category = string.IsNullOrWhiteSpace(Category) ? "Other" : Category.Trim(),
            Bus = IsMsCan ? CanBus.Ms : CanBus.Hs,
            Kind = _kind,
            Mode = (byte)mode,
            Pid = (ushort)pid,
            Module = string.IsNullOrWhiteSpace(ModuleText) ? null : ModuleText.Trim().ToUpperInvariant().Replace("0X", "", StringComparison.Ordinal),
            Decode = new DecodeSpec(byteOffset, _byteLength, IsSigned, scale, offset, (Unit ?? "").Trim(), _mask),
            States = _states,
            DefaultRateHz = rate,
            StalenessSeconds = _stalenessSeconds,
            Min = min,
            Max = max,
            Hidden = _hidden,
            Unconfirmed = _unconfirmed && !ConfirmedByTest,
        };

        problems = SignalCatalog.Check(definition);
        return problems.Count == 0 ? definition : null;
    }

    /// <summary>Why it cannot be saved yet, one line each.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            TryBuild(out var problems);
            return problems;
        }
    }

    /// <summary>True while saving is refused.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>The line under the form — the first problem, or the go-ahead.</summary>
    public string Message => Problems.FirstOrDefault() is { } first
        ? char.ToUpperInvariant(first[0]) + first[1..] + "."
        : _unconfirmed && !ConfirmedByTest
            ? "Typed in the ID matcher and not confirmed yet: TEST it, then save, and the card editor will offer it."
            : "Ready to save. Takes effect at the next launch.";

    private bool CanSave() => !HasProblems;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (TryBuild(out _) is { } definition)
        {
            _save(definition);
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel();

    [RelayCommand]
    private void Remove() => _remove?.Invoke(Id);

    // ── TEST ──────────────────────────────────────────────────────────────────

    /// <summary>The request as the adapter will see it, e.g. <c>01 0C</c> or <c>22 4001 → 726</c>.</summary>
    public string RequestText => TryHex(ModeText, 0xFF, out var mode) && TryHex(PidText, 0xFFFF, out var pid)
        ? string.Create(CultureInfo.InvariantCulture, $"{mode:X2} {(pid <= 0xFF ? pid.ToString("X2", CultureInfo.InvariantCulture) : pid.ToString("X4", CultureInfo.InvariantCulture))}{(SignalDefinition.ParseModule(ModuleText) is { } module ? $" → {module:X3}" : "")}")
        : "—";

    /// <summary>The module typed, or null for the broadcast. False when something unreadable is typed.</summary>
    private bool TryModule(out ushort? module)
    {
        module = SignalDefinition.ParseModule(ModuleText);
        return string.IsNullOrWhiteSpace(ModuleText) || module is not null;
    }

    /// <summary>The payload the last TEST returned, kept so a changed formula re-decodes it.</summary>
    private byte[]? _lastPayload;

    /// <summary>What the last TEST got back, as the bytes after the echoed mode and PID.</summary>
    [ObservableProperty]
    private string _testRaw = "";

    /// <summary>True once a TEST has run, so the result block appears.</summary>
    [ObservableProperty]
    private bool _hasTested;

    /// <summary>True while a TEST is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    /// <summary>
    /// What the current formula makes of the last payload, and whether min/max would let it
    /// through — recomputed as the formula is edited, without asking the truck again.
    /// </summary>
    public string TestDecoded
    {
        get
        {
            if (_lastPayload is null)
            {
                return "";
            }

            var unreadable = new List<string>();
            var byteOffset = ParseInt(ByteOffsetText, "byte offset", unreadable);
            var scale = ParseNumber(ScaleText, "scale", unreadable);
            var offset = ParseNumber(OffsetText, "offset", unreadable);

            if (unreadable.Count > 0 || byteOffset < 0)
            {
                return "Fix the formula above to decode these bytes.";
            }

            var spec = new DecodeSpec(byteOffset, _byteLength, IsSigned, scale, offset, Unit ?? "", _mask);

            if (spec.Decode(_lastPayload) is not { } value)
            {
                return $"Too short: {_lastPayload.Length} byte(s) back, the decode reads {byteOffset + _byteLength}.";
            }

            var min = ParseOptional(MinText, "", []);
            var max = ParseOptional(MaxText, "", []);
            var inRange = (min is null || value >= min) && (max is null || value <= max);
            var shown = _states is { Count: > 0 }
                ? SignalState.NameOf(_states, value) is { } state ? state : string.Create(CultureInfo.InvariantCulture, $"{value:0.###}, which names no state")
                : string.Create(CultureInfo.InvariantCulture, $"{value:0.###} {Unit}").Trim();

            return inRange
                ? $"Decodes to {shown}."
                : $"Decodes to {shown} — outside min/max, so it would be dropped, not shown.";
        }
    }

    private bool CanTest() => !IsTesting && TryHex(ModeText, 0xFF, out var mode) && mode != 0 && TryHex(PidText, 0xFFFF, out _) && TryModule(out _);

    /// <summary>Ask the truck once, with the request as typed.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (!TryHex(ModeText, 0xFF, out var mode) || !TryHex(PidText, 0xFFFF, out var pid) || !TryModule(out var module))
        {
            return;
        }

        IsTesting = true;
        var bus = IsMsCan ? CanBus.Ms : CanBus.Hs;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _probe(new PidRequest((byte)mode, (ushort)pid, bus, module), timeout.Token);

            _lastPayload = response.IsSuccess ? response.Data : null;
            _testedRequest = RequestText;
            TestRaw = response.IsSuccess
                ? string.Join(' ', response.Data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
                : response.Failure switch
                {
                    PidFailure.NoData => $"NO DATA — nothing on {(bus is CanBus.Ms ? "MS" : "HS")}-CAN answered {RequestText}.",
                    PidFailure.Rejected => $"REFUSED — the module is there and said: {(response.NegativeCode is { } code ? Core.Discovery.ModuleScanner.DescribeRefusal(code) : "no")}.",
                    PidFailure.BusError => "BUS ERROR — the adapter reported a problem on the bus.",
                    PidFailure.Timeout => "No reply — is the adapter connected?",
                    _ => "A reply came back that could not be read.",
                };
        }
        catch (OperationCanceledException)
        {
            _lastPayload = null;
            TestRaw = "No reply within five seconds.";
        }
        finally
        {
            IsTesting = false;
            HasTested = true;
            OnPropertyChanged(nameof(TestDecoded));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(ConfirmedByTest));
        }
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static string Format(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    /// <summary>Hex, with or without a leading <c>0x</c>, and spaces tolerated — "22 F4 0D" style.</summary>
    internal static bool TryHex(string? text, int max, out int value)
    {
        var trimmed = (text ?? "").Replace(" ", "", StringComparison.Ordinal).Trim();

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return int.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            && trimmed.Length > 0
            && value >= 0
            && value <= max;
    }

    private static double ParseNumber(string? text, string field, List<string> problems)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        {
            return value;
        }

        problems.Add($"{field} must be a number");
        return double.NaN;
    }

    private static int ParseInt(string? text, string field, List<string> problems)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        problems.Add($"{field} must be a whole number");
        return 0;
    }

    private static double? ParseOptional(string? text, string field, List<string> problems) =>
        string.IsNullOrWhiteSpace(text) ? null : ParseNumber(text, field, problems);
}
