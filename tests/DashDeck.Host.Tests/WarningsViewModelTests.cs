using DashDeck.Abstractions;
using DashDeck.Core.Actions;
using DashDeck.Core.Diagnostics;
using DashDeck.Core.Warnings;
using DashDeck.Host.Settings;
using DashDeck.Host.ViewModels;
using DashDeck.Host.Warnings;
using System.Text.Json;

namespace DashDeck.Host.Tests;

/// <summary>The warning banner, window and Settings block (ADR-0055), without a truck or a screen.</summary>
public sealed class WarningsViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start;
    }

    private sealed class Bus : IVehicleSignals
    {
        private readonly Dictionary<string, SignalValue> _values = new(StringComparer.Ordinal);

        public List<(string Id, double Rate)> Required { get; } = [];

        public int Released { get; private set; }

        public IReadOnlyCollection<string> KnownSignals => _values.Keys;

        public void Set(string id, double value, SignalQuality quality = SignalQuality.Live) =>
            _values[id] = new SignalValue(id, value, "", Start, quality);

        public SignalValue Current(string signalId) =>
            _values.TryGetValue(signalId, out var v) ? v : SignalValue.Missing(signalId);

        public IDisposable Subscribe(string signalId, Action<SignalValue> onValue) => throw new NotSupportedException();

        public ISignalSubscription Require(string signalId, SignalPriority priority, double rateHz)
        {
            Required.Add((signalId, rateHz));
            return new Demand(this, signalId, rateHz);
        }

        private sealed class Demand(Bus bus, string id, double rate) : ISignalSubscription
        {
            public string SignalId => id;

            public double RequestedRateHz => rate;

            public double EffectiveRateHz => rate;

            public event Action<double>? EffectiveRateChanged { add { } remove { } }

            public void Dispose() => bus.Released++;
        }
    }

    private sealed class Diagnostics : IVehicleDiagnostics
    {
        public TroubleCodeReport Report { get; set; } = new(
            [new ReportedCode(new TroubleCode(0x0420), TroubleCodeKind.Stored, 0x7E0, "7E0 Engine")],
            ["7E0 Engine"],
            Start);

        public int Reads { get; private set; }

        public List<ActionConfirmation> Clears { get; } = [];

        public string? LastShown { get; private set; }

        public TroubleCodeDescriptions Descriptions => TroubleCodeDescriptions.Empty;

        public bool ClearAllowed { get; set; }

        public string ActionLogPath => "actions.log";

        public Task<TroubleCodeReport> ReadCodesAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Report);
        }

        public Task<ActionInterlock> CheckClearAsync(CancellationToken ct) =>
            Task.FromResult(new ActionInterlock(true, "ok", 0, 0));

        public Task<ActionResult> ClearCodesAsync(ActionConfirmation confirmation, string shown, CancellationToken ct)
        {
            Clears.Add(confirmation);
            LastShown = shown;
            Report = new TroubleCodeReport([], ["7E0 Engine"], Start);
            return Task.FromResult(new ActionResult(ActionOutcome.Done, "Codes cleared."));
        }
    }

    private sealed class Preferences : IWarningPreferences
    {
        public Dictionary<string, bool> Stored { get; } = [];

        public Dictionary<string, double?> Saved { get; set; } = [];

        public IReadOnlyDictionary<string, bool> Popups => Stored;

        public void SetPopup(string id, bool? popup)
        {
            if (popup is { } p)
            {
                Stored[id] = p;
            }
            else
            {
                Stored.Remove(id);
            }
        }

        public IReadOnlyDictionary<string, double?> Dismissals => Saved;

        public void SaveDismissals(IReadOnlyDictionary<string, double?> dismissals) => Saved = new(dismissals);
    }

    private static readonly WarningDefinition Engine = new()
    {
        Id = "checkEngine", Title = "CHECK ENGINE", Reason = "ENGINE", Icon = "checkEngine",
        Signal = "diagnostics.checkEngine", CountSignal = "diagnostics.dtcCount", HoldSeconds = 2, Popup = true, Codes = true,
    };

    private static readonly WarningDefinition Fuel = new()
    {
        Id = "lowFuel", Title = "LOW FUEL", Icon = "fuel", Signal = "fuel.levelPercent", Below = 12, HoldSeconds = 2, Popup = false,
    };

    private sealed record Rig(WarningsViewModel Vm, Bus Bus, Diagnostics Diagnostics, Preferences Preferences, Clock Clock)
    {
        public bool AllowClear { get; set; }

        public void Beat(int seconds = 1)
        {
            for (var i = 0; i < seconds; i++)
            {
                Vm.Refresh();
                Clock.UtcNow += TimeSpan.FromSeconds(1);
            }
        }
    }

    private static Rig Make(Preferences? preferences = null)
    {
        var bus = new Bus();
        var diagnostics = new Diagnostics();
        var clock = new Clock();
        preferences ??= new Preferences();
        Rig? rig = null;
        var vm = new WarningsViewModel(bus, [Engine, Fuel], diagnostics, preferences, clock, () => rig!.AllowClear, on => rig!.AllowClear = on);
        rig = new Rig(vm, bus, diagnostics, preferences, clock);
        bus.Set("vehicle.speed", 0);
        bus.Set("diagnostics.dtcCount", 1);
        return rig;
    }

    [Fact]
    public void Only_the_lights_that_pop_are_asked_for_and_the_speed()
    {
        var rig = Make();

        Assert.Equal(["diagnostics.checkEngine", "diagnostics.dtcCount"], rig.Vm.WatchedSignals);
        Assert.Contains(rig.Bus.Required, r => r.Id == "vehicle.speed");
        Assert.DoesNotContain(rig.Bus.Required, r => r.Id == "fuel.levelPercent");
        Assert.All(rig.Bus.Required.Where(r => r.Id != "vehicle.speed"), r => Assert.Equal(WarningsViewModel.LightRateHz, r.Rate));
    }

    [Fact]
    public void Stopped_it_is_the_window_with_the_codes_read()
    {
        var rig = Make();
        rig.Bus.Set("diagnostics.checkEngine", 1);

        rig.Beat(3);

        Assert.True(rig.Vm.ShowWindow);
        Assert.False(rig.Vm.ShowBanner);
        Assert.Equal("CHECK ENGINE", rig.Vm.Title);
        Assert.NotEmpty(rig.Vm.IconData);
        Assert.Equal(1, rig.Diagnostics.Reads);
        Assert.Equal("P0420", rig.Vm.Codes.Single().Code);

        // Read once per alert, not on every beat.
        rig.Beat(5);
        Assert.Equal(1, rig.Diagnostics.Reads);
    }

    [Fact]
    public void Moving_it_is_the_banner_and_no_codes_are_read()
    {
        var rig = Make();
        rig.Bus.Set("vehicle.speed", 60);
        rig.Bus.Set("diagnostics.checkEngine", 1);

        rig.Beat(3);

        Assert.True(rig.Vm.ShowBanner);
        Assert.False(rig.Vm.ShowWindow);
        Assert.Equal("ENGINE", rig.Vm.BannerWord);
        Assert.Equal(0, rig.Diagnostics.Reads);

        // Stopping turns the banner into the window.
        rig.Bus.Set("vehicle.speed", 0);
        rig.Beat();
        Assert.True(rig.Vm.ShowWindow);
    }

    [Fact]
    public void Dismissed_it_stays_quiet_and_is_remembered()
    {
        var rig = Make();
        rig.Bus.Set("diagnostics.checkEngine", 1);
        rig.Beat(3);

        rig.Vm.DismissCommand.Execute(null);
        rig.Beat(10);

        Assert.False(rig.Vm.HasAlert);
        Assert.True(rig.Preferences.Saved.ContainsKey("checkEngine"));
        Assert.Equal("LIT · DISMISSED", rig.Vm.Rows.Single(r => r.Definition.Id == "checkEngine").Status);

        // A new launch remembers it.
        var again = Make(rig.Preferences);
        again.Bus.Set("diagnostics.checkEngine", 1);
        again.Beat(5);
        Assert.False(again.Vm.HasAlert);

        // A new code brings it back.
        again.Bus.Set("diagnostics.dtcCount", 2);
        again.Beat();
        Assert.True(again.Vm.HasAlert);
    }

    [Fact]
    public void A_popup_switched_on_is_asked_for_and_pops()
    {
        var rig = Make();
        rig.Bus.Set("fuel.levelPercent", 5);
        rig.Beat(4);
        Assert.False(rig.Vm.HasAlert);

        rig.Vm.TogglePopupCommand.Execute(rig.Vm.Rows.Single(r => r.Definition.Id == "lowFuel"));
        Assert.Contains("fuel.levelPercent", rig.Vm.WatchedSignals);
        Assert.True(rig.Preferences.Stored["lowFuel"]);
        Assert.True(rig.Vm.HasAlert);
        Assert.Equal("LOW FUEL", rig.Vm.Title);

        // Back to the file's default: nothing kept.
        rig.Vm.TogglePopupCommand.Execute(rig.Vm.Rows.Single(r => r.Definition.Id == "lowFuel"));
        Assert.False(rig.Preferences.Stored.ContainsKey("lowFuel"));
    }

    [Fact]
    public async Task Clearing_is_offered_only_with_the_master_switch_on_and_reads_back()
    {
        var rig = Make();
        rig.Bus.Set("diagnostics.checkEngine", 1);
        rig.Beat(3);

        Assert.False(rig.Vm.OffersClear);
        Assert.True(rig.Vm.IsClearOff);

        rig.Vm.ToggleAllowClearCommand.Execute(null);
        Assert.True(rig.AllowClear);
        Assert.True(rig.Vm.OffersClear);
        Assert.Equal("ALLOW CLEARING CODES — ON", rig.Vm.AllowClearLabel);

        await rig.Vm.ClearCodesCommand.ExecuteAsync(TimeSpan.FromSeconds(2.3));

        var hold = Assert.Single(rig.Diagnostics.Clears);
        Assert.Equal(TimeSpan.FromSeconds(2.3), hold.HeldFor);
        Assert.Equal(rig.Clock.UtcNow, hold.At);
        Assert.Contains("P0420", rig.Diagnostics.LastShown, StringComparison.Ordinal);
        Assert.Equal("Codes cleared.", rig.Vm.ClearStatus);
        Assert.Empty(rig.Vm.Codes);
        Assert.StartsWith("No codes", rig.Vm.CodesStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Shipped_warnings_have_icons_the_console_draws()
    {
        var (warnings, problems) = WarningCatalog.Load(CatalogPath.Find(WarningCatalog.FileName));
        Assert.Empty(problems);
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.NotEmpty(WarningsViewModel.IconFor(w.Icon)));
    }

    [Fact]
    public void Clearing_codes_is_off_in_a_settings_file_that_never_mentioned_it()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var older = JsonSerializer.Deserialize<UserSettings>("""{ "themeMode": "Day" }""", options)!;

        Assert.False(older.AllowClearCodes);
        Assert.Empty(older.WarningPopups);
        Assert.Empty(older.DismissedWarnings);
    }
}
