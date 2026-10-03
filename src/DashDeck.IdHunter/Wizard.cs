using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery;

namespace DashDeck.IdHunter;

/// <summary>
/// The guide: a checklist of things to find, and for each one the steps, the ranking and a live
/// check (ADR-0044). Reads only — it asks modules for identifiers and listens silently.
/// </summary>
internal sealed partial class Wizard
{
    private readonly IHuntConsole _io;
    private readonly HuntSession _session;
    private readonly IReadOnlyList<HuntTarget> _targets;
    private readonly HuntOutput _output;
    private readonly IReadOnlyList<VehiclePack> _packs;
    private readonly IClock _clock;
    private readonly Dictionary<string, string> _status = [];

    public Wizard(IHuntConsole io, HuntSession session, IReadOnlyList<HuntTarget> targets, HuntOutput output, IReadOnlyList<VehiclePack> packs, IClock? clock = null)
    {
        _io = io;
        _session = session;
        _targets = targets;
        _output = output;
        _packs = packs;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Seconds each listen step is held; the checklist's own unless set (the tests shorten it).</summary>
    public int? HoldSeconds { get; init; }

    /// <summary>The longest a follow watches, in minutes; the checklist's own unless set.</summary>
    public double? FollowMinutes { get; init; }

    public async Task RunAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("DashDeck ID HUNTER — finds where the truck keeps a value.");
        _io.WriteLine("Reads only: it asks modules for identifiers and listens to the bus silently.");
        _io.WriteLine("Nothing it sends changes anything on the truck.");
        _io.WriteLine();
        _io.WriteLine($"Adapter:  {_session.Description}");
        _io.WriteLine($"Writing:  {_output.Folder}");
        _io.WriteLine();
        _io.WriteLine("Checking the bus on OBD pins 3 and 11 — listening only, so nothing is sent at a wrong speed…");
        var pins311 = await _session.DetectPins311Async(ct).ConfigureAwait(false);
        _io.WriteLine(pins311 is { } rate
            ? $"Pins 3/11 carry a {rate / 1000} kbit/s bus{(rate == 125000 ? " (MS-CAN)" : "")}. The guide will use it at that speed."
            : "Nothing heard on pins 3/11 at 125 or 500 kbit/s (is the ignition on?). The guide uses HS-CAN only, and sends nothing on pins 3/11.");

        while (!ct.IsCancellationRequested)
        {
            ShowMenu();
            var choice = Ask("Choose").ToUpperInvariant();

            if (choice is "Q" or "QUIT" or "EXIT")
            {
                break;
            }

            try
            {
                if (choice == "S")
                {
                    await ScanModulesAsync(ct).ConfigureAwait(false);
                }
                else if (choice == "L")
                {
                    await FreeListenAsync(ct).ConfigureAwait(false);
                }
                else if (choice == "A")
                {
                    await FreeAskStepsAsync(ct).ConfigureAwait(false);
                }
                else if (choice == "D")
                {
                    await CheckDatabaseAsync(ct).ConfigureAwait(false);
                }
                else if (int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= _targets.Count)
                {
                    var target = _targets[n - 1];
                    await (target.Method switch
                    {
                        "listen" => ListenAsync(target, ct),
                        "follow" => FollowAsync(target, ct),
                        _ => MatchAsync(target, ct),
                    }).ConfigureAwait(false);
                }
                else if (choice.Length > 0)
                {
                    _io.WriteLine("Type a number from the list, S, L, A, D or Q.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                _io.WriteLine();
                _io.WriteLine($"The adapter went away: {ex.Message}");
                _io.WriteLine("Check the cable, then choose the target again.");
            }
        }

        _io.WriteLine();
        _io.WriteLine(File.Exists(_output.FindingsPath)
            ? $"Done. Send this file: {_output.FindingsPath}"
            : "Done. Nothing was ranked, so there is no findings.csv yet.");
    }

    private void ShowMenu()
    {
        _io.WriteLine();
        _io.WriteLine("  #   WHAT TO FIND                    HOW      FOUND");

        for (var i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1,-3} {t.Name,-31} {t.Method,-8} {_status.GetValueOrDefault(t.Id, "—")}"));
        }

        _io.WriteLine();
        _io.WriteLine("  S   Scan for modules (what is on each bus)");
        _io.WriteLine("  L   Listen freely — name your own action");
        _io.WriteLine("  A   Ask a module while you do something — for what is never broadcast");
        _io.WriteLine("  D   Check a CAN database (.dbc file) against the truck");
        _io.WriteLine("  Q   Quit");
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    private string Ask(string prompt)
    {
        _io.Write($"{prompt}: ");
        return (_io.ReadLine() ?? "q").Trim();
    }

    /// <summary>Wait for Enter. False when the person typed B (back) or Q.</summary>
    private bool Proceed(string prompt = "Press Enter when ready, or B to go back")
    {
        var answer = Ask(prompt).ToUpperInvariant();
        return answer is not ("B" or "Q" or "BACK");
    }

    private string Verdict()
    {
        while (true)
        {
            var answer = Ask("Did it follow what you did?  y = yes   n = no   ? = not sure").ToLowerInvariant();
            switch (answer)
            {
                case "y" or "yes": return "confirmed";
                case "n" or "no": return "rejected";
                case "?" or "" or "not sure": return "unsure";
            }
        }
    }

    /// <summary>Refuse to go on while the truck is moving; warn when it does not say.</summary>
    private async Task<bool> ParkedAsync(CancellationToken ct)
    {
        // Asked up to three times: one unanswered request is routine, and should not stop the guide.
        double? speed = null;
        for (var attempt = 0; attempt < 3 && speed is null; attempt++)
        {
            speed = await _session.SpeedAsync(ct).ConfigureAwait(false);
        }

        if (speed is > 0)
        {
            _io.WriteLine($"The truck says it is moving ({speed} km/h). Stop and park first — this is never done on the road.");
            return false;
        }

        if (speed is null)
        {
            _io.WriteLine("The truck did not answer its speed. Is the ignition on?");
            return Proceed("Press Enter to carry on anyway, or B to go back");
        }

        return true;
    }

    private string? ModuleName(ushort address) => ModuleNames.Likely(address, _packs);

    private static string Hex(ushort value) => value.ToString("X3", CultureInfo.InvariantCulture);

    private string Bus(CanBus bus) => _session.BusName(bus);

    // ── Modules ──────────────────────────────────────────────────────────────

    private async Task ScanModulesAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("SCAN FOR MODULES — asks every module address on both buses for its part number.");
        _io.WriteLine("Needs: ignition ON, parked. About half a minute.");
        if (!Proceed() || !await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var progress = new Progress(_io);
        if (_session.Pins311BitRate is null)
        {
            _io.WriteLine("Pins 3/11 were silent, so only HS-CAN is asked.");
        }

        var result = await ModuleScanner.ScanAsync(_session.RequestAsync, _session.RequestBuses, progress, ct).ConfigureAwait(false);
        progress.Done();

        using var capture = _output.Capture($"modules-{_clock.UtcNow.ToLocalTime():HHmmss}", "bus,module,likely,part_number,refusal");
        foreach (var module in result.Modules)
        {
            _session.KnownModules[module.Address] = module.Bus;
            var name = ModuleName(module.Address);
            _io.WriteLine($"  {Bus(module.Bus)}  {Hex(module.Address)}  {(name is null ? "" : name + " (likely)"),-34} {module.PartNumber ?? (module.RefusalCode is { } c ? ModuleScanner.DescribeRefusal(c) : "")}");
            capture.WriteLine(HuntOutput.Row(Bus(module.Bus), Hex(module.Address), name, module.PartNumber, module.RefusalCode));
        }

        _io.WriteLine(result.Modules.Count == 0 ? "No module answered. Is the ignition on?" : $"{result.Modules.Count} modules.");
    }

    /// <summary>Ask which module to search, suggesting the checklist's, and find its bus.</summary>
    private async Task<(ushort Module, CanBus Bus)?> ChooseModuleAsync(HuntTarget target, CancellationToken ct)
    {
        var suggested = target.Modules.Count > 0 ? target.Modules[0] : null;
        if (suggested is not null)
        {
            _io.WriteLine("Which module to search? Likely ones for this:");
            foreach (var m in target.Modules)
            {
                var address = ushort.Parse(m, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _io.WriteLine($"  {m}  {ModuleName(address) ?? "?"}");
            }
        }
        else
        {
            _io.WriteLine("Which module to ask? FORScan's module list names the one that runs it (SCME for the seats,");
            _io.WriteLine("for example) — match it by part number against S on the main menu, which lists what answered:");
            foreach (var (address, bus) in _session.KnownModules.OrderBy(m => m.Key))
            {
                _io.WriteLine($"  {Hex(address)}  {Bus(bus),-22} {ModuleName(address) ?? ""}");
            }
        }

        while (true)
        {
            var typed = Ask(suggested is null ? "Module address, or B to go back" : $"Module address [{suggested}], or B to go back");
            if (typed.Equals("B", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (typed.Length == 0)
            {
                if (suggested is null)
                {
                    continue;
                }

                typed = suggested;
            }

            if (!ushort.TryParse(typed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var module) ||
                !Vehicle.PidRequest.IsModuleAddress(module))
            {
                _io.WriteLine("A module address is three hex digits from 700 to 7F7, like 7E0.");
                continue;
            }

            var bus = await _session.FindModuleAsync(module, ct).ConfigureAwait(false);
            if (bus is null)
            {
                _io.WriteLine($"{Hex(module)} did not answer on either bus. Is the ignition on? Try another, or S on the main menu to see what is there.");
                continue;
            }

            _io.WriteLine($"{Hex(module)} answers on {Bus(bus.Value)}.");
            return (module, bus.Value);
        }
    }

    /// <summary>Ask which identifier ranges to sweep, and sweep them.</summary>
    private async Task<List<FoundIdentifier>?> SweepAsync(HuntTarget target, ushort module, CanBus bus, CancellationToken ct)
    {
        var defaults = _session.Simulated && target.SimulateRanges.Count > 0 ? target.SimulateRanges : target.Ranges;
        var suggestion = defaults.Count > 0 ? string.Join(",", defaults) : "1000-1FFF";

        List<(ushort First, ushort Last)> ranges;
        while (true)
        {
            var typed = Ask($"Identifier ranges to sweep [{suggestion}]");
            if (typed.Length == 0)
            {
                typed = suggestion;
            }

            if (ParseRanges(typed) is { } parsed)
            {
                ranges = parsed;
                break;
            }

            _io.WriteLine("Ranges look like 1000-1FFF, at most 1000 (hex) identifiers each, separated by commas.");
        }

        var total = ranges.Sum(r => r.Last - r.First + 1);
        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{total} identifiers: about {Math.Ceiling(total / 19.0 / 60.0)} minute(s). Keep the ignition on and stay parked."));
        if (!Proceed("Press Enter to sweep, or B to go back") || !await ParkedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var found = new List<FoundIdentifier>();
        foreach (var (first, last) in ranges)
        {
            var progress = new Progress(_io);
            var result = await DidScanner.ScanAsync(_session.RequestAsync, bus, module, first, last, progress, ct).ConfigureAwait(false);
            progress.Done();
            found.AddRange(result.Found);

            using var capture = _output.Capture($"sweep-{Hex(module)}-{first:X4}-{last:X4}", "did,bytes,data,refusal");
            foreach (var f in result.Found)
            {
                capture.WriteLine(HuntOutput.Row(f.Did.ToString("X4", CultureInfo.InvariantCulture), f.Data.Length, Convert.ToHexString(f.Data), f.RefusalCode));
            }

            _io.WriteLine($"  {first:X4}–{last:X4}: {result.Found.Count(f => f.RefusalCode is null)} answered{(result.Problem is { } p ? $" ({p})" : "")}.");
        }

        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return found;
    }

    internal static List<(ushort First, ushort Last)>? ParseRanges(string text)
    {
        var ranges = new List<(ushort, ushort)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length != 2 ||
                !ushort.TryParse(ends[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) ||
                !ushort.TryParse(ends[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var last) ||
                last < first || last - first + 1 > DidScanner.MaxSpan)
            {
                return null;
            }

            ranges.Add((first, last));
        }

        return ranges.Count > 0 ? ranges : null;
    }

    /// <summary>A one-line progress readout that rewrites itself.</summary>
    private sealed class Progress(IHuntConsole io) : IProgress<SweepProgress>
    {
        private int _lastPercent = -1;

        public void Report(SweepProgress value)
        {
            var percent = value.Total == 0 ? 100 : value.Done * 100 / value.Total;
            if (percent / 5 != _lastPercent / 5)
            {
                _lastPercent = percent;
                io.Write($"\r  {percent,3}%  found {value.Found,-5} {value.Current,-12}");
            }
        }

        public void Done() => io.WriteLine();
    }
}
