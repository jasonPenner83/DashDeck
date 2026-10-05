using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Actions;
using DashDeck.Core.Diagnostics;
using DashDeck.Core.Discovery;
using DashDeck.Core.Warnings;
using DashDeck.Host.Stage.Gauges;
using DashDeck.Host.Warnings;

namespace DashDeck.Host.ViewModels;

/// <summary>One trouble code as the window and Settings list it.</summary>
public sealed record CodeRow(string Code, string Description, string Where, bool IsPending)
{
    public string Kind => IsPending ? "PENDING" : "STORED";
}

/// <summary>One warning in Settings ▸ Diagnostics: what it is, whether it is lit, whether it pops up.</summary>
public sealed partial class WarningRow : ObservableObject
{
    public WarningRow(WarningDefinition definition) => Definition = definition;

    public WarningDefinition Definition { get; }

    public string Title => Definition.Title;

    public string IconData => WarningsViewModel.IconFor(Definition.Icon);

    public bool IsStop => Definition.Severity == WarningSeverity.Stop;

    [ObservableProperty]
    private bool _popup;

    [ObservableProperty]
    private string _status = "NO READING";

    public string PopupLabel => Popup ? "POPUP ON" : "POPUP OFF";

    partial void OnPopupChanged(bool value) => OnPropertyChanged(nameof(PopupLabel));
}

/// <summary>
/// The warning lights (ADR-0055): a banner across the top while moving, a window when stopped, the
/// trouble codes, and clearing them through the gated choke point.
/// </summary>
/// <remarks>
/// Driven by the shell's one-second beat (<see cref="Refresh"/>), on the UI thread — the monitor
/// reads what the bus already holds and asks the truck nothing itself. What it watches is declared
/// to the arbiter like any card: the lights whose popup is on, slowly, and the speed, so a warning
/// that is switched off costs nothing.
/// </remarks>
public sealed partial class WarningsViewModel : ObservableObject, IDisposable
{
    /// <summary>How often a watched light is asked for. Slow: a warning has a hold time of seconds anyway.</summary>
    public const double LightRateHz = 0.25;

    /// <summary>How often the speed is asked for, to choose the banner or the window.</summary>
    public const double SpeedRateHz = 0.5;

    private readonly IVehicleSignals _signals;
    private readonly IVehicleDiagnostics _diagnostics;
    private readonly IWarningPreferences _preferences;
    private readonly IClock _clock;
    private readonly Func<bool> _getAllowClear;
    private readonly Action<bool> _setAllowClear;
    private readonly WarningMonitor _monitor;
    private readonly List<IDisposable> _demand = [];
    private IReadOnlyDictionary<string, double?> _savedDismissals;
    private string? _codesReadFor;
    private bool _busy;

    public WarningsViewModel(
        IVehicleSignals signals,
        IReadOnlyList<WarningDefinition> definitions,
        IVehicleDiagnostics diagnostics,
        IWarningPreferences preferences,
        IClock clock,
        Func<bool> getAllowClear,
        Action<bool> setAllowClear,
        IReadOnlyList<string>? problems = null)
    {
        _signals = signals;
        _diagnostics = diagnostics;
        _preferences = preferences;
        _clock = clock;
        _getAllowClear = getAllowClear;
        _setAllowClear = setAllowClear;
        _monitor = new WarningMonitor(definitions);

        _savedDismissals = preferences.Dismissals;
        _monitor.Restore(_savedDismissals);

        var popups = preferences.Popups;
        foreach (var definition in definitions)
        {
            Rows.Add(new WarningRow(definition) { Popup = popups.TryGetValue(definition.Id, out var on) ? on : definition.Popup });
        }

        Problems = problems is { Count: > 0 } ? string.Join(" ", problems) : "";
        Declare();
    }

    /// <summary>Every warning, for Settings.</summary>
    public ObservableCollection<WarningRow> Rows { get; } = [];

    /// <summary>The codes last read.</summary>
    public ObservableCollection<CodeRow> Codes { get; } = [];

    /// <summary>What went wrong reading the warnings file, if anything.</summary>
    public string Problems { get; }

    // ── What is on screen ────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlert), nameof(ShowBanner), nameof(ShowWindow), nameof(Title), nameof(BannerWord),
        nameof(Advice), nameof(IconData), nameof(IsStop), nameof(ShowsCodes), nameof(OffersClear))]
    private WarningState? _alert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner), nameof(ShowWindow))]
    private bool _isMoving;

    public bool HasAlert => Alert is not null;

    /// <summary>Moving: a strip across the top, one word, and DISMISS.</summary>
    public bool ShowBanner => HasAlert && IsMoving;

    /// <summary>Stopped: the window, with the advice and the codes.</summary>
    public bool ShowWindow => HasAlert && !IsMoving;

    public string Title => Alert?.Definition.Title ?? "";

    public string BannerWord => Alert?.Definition.BannerWord ?? "";

    public string Advice => Alert?.Definition.Advice ?? "";

    public string IconData => Alert is { } a ? IconFor(a.Definition.Icon) : "";

    /// <summary>Red rather than amber.</summary>
    public bool IsStop => Alert?.Definition.Severity == WarningSeverity.Stop;

    /// <summary>The window lists the trouble codes.</summary>
    public bool ShowsCodes => Alert?.Definition.Codes == true;

    /// <summary>The window offers CLEAR CODES: it shows codes, and the master switch is on.</summary>
    public bool OffersClear => ShowsCodes && _getAllowClear();

    [ObservableProperty]
    private string _codesStatus = "Not read yet.";

    [ObservableProperty]
    private string _clearStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isWorking;

    public bool IsIdle => !IsWorking;

    /// <summary>What clearing does, said every time it is offered.</summary>
    public static string ClearCaveats =>
        "Clearing turns the check-engine light off and erases the codes, the freeze frame and the readiness monitors — an emissions inspection will fail until they have run again over a few days of driving. If the fault is still there the light comes back. Only when stopped, with the engine off and the ignition on.";

    /// <summary>Gate 2, the master switch, as the panel shows it.</summary>
    public bool IsClearAllowed => _getAllowClear();

    public bool IsClearOff => !_getAllowClear();

    public string AllowClearLabel => _getAllowClear() ? "ALLOW CLEARING CODES — ON" : "ALLOW CLEARING CODES — OFF";

    public string ActionLogPath => _diagnostics.ActionLogPath;

    // ── The beat ─────────────────────────────────────────────────────────────

    /// <summary>Look at the lights. Called on the shell's one-second beat.</summary>
    public void Refresh()
    {
        var speed = _signals.Current(SignalProbe.Speed);
        double? kph = speed.IsUsable && !double.IsNaN(speed.Value) ? speed.Value : null;

        var changes = _monitor.Update(_clock.UtcNow, _signals.Current, kph);
        IsMoving = kph is { } k && k > WarningMonitor.MovingKph;

        var states = _monitor.States;
        foreach (var row in Rows)
        {
            var state = states.FirstOrDefault(s => s.Id == row.Definition.Id);
            var reading = _signals.Current(row.Definition.Signal);
            row.Status = !reading.IsUsable ? "NO READING"
                : state is { IsLit: true, IsDismissed: true } ? "LIT · DISMISSED"
                : state is { IsLit: true } ? "LIT"
                : "OFF";
        }

        Alert = _monitor.Alert(PopupFor);

        // A new code while the window is up: read them again.
        if (changes.Any(c => c.Change == WarningChange.Reraised))
        {
            _codesReadFor = null;
        }

        if (ShowWindow && ShowsCodes && _codesReadFor != AlertKey)
        {
            _codesReadFor = AlertKey;
            _ = ReadCodesAsync();
        }

        SaveDismissalsIfChanged();
    }

    private string? AlertKey => Alert is { } a ? $"{a.Id}@{a.LitSince:O}#{a.Count}" : null;

    private bool PopupFor(WarningDefinition definition) =>
        Rows.FirstOrDefault(r => r.Definition.Id == definition.Id)?.Popup ?? definition.Popup;

    private void SaveDismissalsIfChanged()
    {
        var now = _monitor.Dismissals;
        if (now.Count == _savedDismissals.Count && now.All(p => _savedDismissals.TryGetValue(p.Key, out var v) && v == p.Value))
        {
            return;
        }

        _preferences.SaveDismissals(now);
        _savedDismissals = now;
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>The driver has seen it. It stays quiet until the light goes off and on, or a new code.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (Alert is { } alert)
        {
            _monitor.Dismiss(alert.Id);
            ClearStatus = "";
            Alert = _monitor.Alert(PopupFor);
            SaveDismissalsIfChanged();
        }
    }

    [RelayCommand]
    private Task ReadCodes() => ReadCodesAsync();

    private async Task ReadCodesAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        IsWorking = true;
        CodesStatus = "Reading the codes…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var report = await _diagnostics.ReadCodesAsync(timeout.Token);
            Show(report);
        }
        catch (OperationCanceledException)
        {
            CodesStatus = "No answer in time. Is the ignition on?";
        }
        finally
        {
            _busy = false;
            IsWorking = false;
        }
    }

    private void Show(TroubleCodeReport report)
    {
        Codes.Clear();
        foreach (var code in report.Codes)
        {
            Codes.Add(new CodeRow(
                code.Text,
                _diagnostics.Descriptions.Describe(code.Code),
                code.ModuleName,
                code.IsPending));
        }

        _lastShown = report.AnyAnswered ? report.Summary : "nothing read";
        CodesStatus = !report.AnyAnswered
            ? "No module answered. Is the ignition on?"
            : report.Codes.Count == 0
                ? $"No codes, stored or pending ({string.Join(", ", report.Answered)} answered)."
                : $"{report.Stored.Count()} stored, {report.Pending.Count()} pending — {string.Join(", ", report.Answered)} answered.";
    }

    private string _lastShown = "nothing read";

    /// <summary>The hold finished: clear the codes, if every gate agrees.</summary>
    [RelayCommand]
    private async Task ClearCodes(TimeSpan held)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        IsWorking = true;
        ClearStatus = "Checking the truck is stopped with the engine off…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await _diagnostics.ClearCodesAsync(new ActionConfirmation(held, _clock.UtcNow), _lastShown, timeout.Token);
            ClearStatus = result.Message;

            if (result.Sent)
            {
                // Read back what is there now, so the screen shows the result rather than claiming it.
                var report = await _diagnostics.ReadCodesAsync(timeout.Token);
                Show(report);
            }
        }
        catch (OperationCanceledException)
        {
            ClearStatus = "No answer in time. Read the codes again to see where things stand.";
        }
        finally
        {
            _busy = false;
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void ToggleAllowClear()
    {
        _setAllowClear(!_getAllowClear());
        OnPropertyChanged(nameof(AllowClearLabel));
        OnPropertyChanged(nameof(OffersClear));
        OnPropertyChanged(nameof(IsClearAllowed));
        OnPropertyChanged(nameof(IsClearOff));
    }

    [RelayCommand]
    private void TogglePopup(WarningRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.Popup = !row.Popup;
        _preferences.SetPopup(row.Definition.Id, row.Popup == row.Definition.Popup ? null : row.Popup);
        Declare();
        Alert = _monitor.Alert(PopupFor);
    }

    // ── Demand ───────────────────────────────────────────────────────────────

    /// <summary>Ask for what the popups need — and only that — as any card does (ADR-0004).</summary>
    private void Declare()
    {
        foreach (var d in _demand)
        {
            d.Dispose();
        }

        _demand.Clear();

        var wanted = Rows.Where(r => r.Popup).Select(r => r.Definition).ToList();
        var ids = wanted.SelectMany(d => new[] { d.Signal, d.CountSignal }).OfType<string>().Distinct().ToList();
        foreach (var id in ids)
        {
            _demand.Add(_signals.Require(id, SignalPriority.Low, LightRateHz));
        }

        _demand.Add(_signals.Require(SignalProbe.Speed, SignalPriority.Low, SpeedRateHz));
        WatchedSignals = ids;
    }

    /// <summary>The lights and counts asked for now, for tests and Settings.</summary>
    public IReadOnlyList<string> WatchedSignals { get; private set; } = [];

    /// <summary>A console icon's path data, with the even-odd rule written into it where the icon needs it.</summary>
    public static string IconFor(string name) =>
        WarningIcons.Find(name) is { } icon ? (icon.EvenOdd ? "F0 " : "F1 ") + icon.Path : "";

    public void Dispose()
    {
        foreach (var d in _demand)
        {
            d.Dispose();
        }

        _demand.Clear();
    }
}
