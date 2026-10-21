using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle;

namespace DashDeck.IdHunter;

/// <summary>
/// M on the main menu: match a number named on the spot, read as many times as it changes
/// (ADR-0069), in as many places at once as the person likes (ADR-0070).
/// </summary>
/// <remarks>
/// The checklist's match targets each name one thing and a fixed number of rounds. This is the same
/// engine with the target made up at the menu — cruise control's set speed, say — and the rounds left
/// open, because the person knows better than a file how many different values they can produce.
/// <para>
/// <b>Not knowing where it lives is the usual case</b>, so one match looks in several places: any
/// number of modules, swept once beforehand, and the bus itself, heard for a few seconds at every
/// reading. Every place is read at every reading and the ranking is one table across all of them, so a
/// single drive with two set speeds covers every candidate.
/// </para>
/// <para>
/// Some numbers only exist on the road — a set speed needs cruise control set — so nothing here asks
/// whether the truck is moving. Each reading's speed is recorded in the capture instead, so a reading
/// taken on the road is visibly one.
/// </para>
/// </remarks>
internal sealed partial class Wizard
{
    /// <summary>The most readings one match takes; enough for any number a person can make vary.</summary>
    internal const int MaxFreeReadings = 12;

    /// <summary>
    /// Under <c>--simulate</c>, the set speeds the synthetic truck's cruise takes, one per reading, so M
    /// can be practised at a desk. Null on a real truck and in the tests, which set it themselves.
    /// </summary>
    public IReadOnlyList<double>? PracticeCruise { get; init; }

    /// <summary>How long each reading listens to each bus, in seconds; the tests shorten it.</summary>
    public double ListenSeconds { get; init; } = 3;

    /// <summary>A free match's sweeps, kept between visits so a second try need not sweep again.</summary>
    private readonly Dictionary<string, List<MatchPlace>> _placesSwept = [];

    /// <summary>
    /// One place a free match looks: a swept module's identifiers, or what a bus broadcasts.
    /// </summary>
    private sealed record MatchPlace(CanBus Bus, ushort? Module, IdentifierWatch? Watch)
    {
        public bool IsBroadcast => Module is null;

        public MatchTally Tally { get; } = new();
    }

    private async Task FreeMatchAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("MATCH A NUMBER YOU NAME — look in modules and on the bus, then type the number each time it changes.");
        _io.WriteLine("For a number you can see but FORScan does not list: cruise control's set speed, a trip meter, a setting.");

        var name = Ask("What is it called (like Cruise set speed), or B to go back");
        if (name.Length == 0 || name.Equals("B", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var target = new HuntTarget
        {
            Id = "free." + Slug(name),
            Name = name,
            Method = "match",
            Needs = "Ignition ON. The readings can come later, wherever the number is — cruise needs the road.",
        };

        if (await ChoosePlacesAsync(target, ct).ConfigureAwait(false) is not { Count: > 0 } places)
        {
            return;
        }

        using var capture = _output.Capture($"match-{target.Id}-{_clock.UtcNow.ToLocalTime():HHmmss}", "round,where,label,shown,id,data,km_h");

        _io.WriteLine();
        _io.WriteLine("Now make the number show, and type it exactly as shown. Then change it and type it again.");
        _io.WriteLine("Two different numbers usually settle it; three make it sure. Cruise: set 60, type 60; set 100, type 100.");

        var readings = 0;
        var seen = new List<double>();
        while (readings < MaxFreeReadings)
        {
            if (PracticeCruise is { Count: > 0 } practice)
            {
                var set = practice[readings % practice.Count];
                _session.Simulate(new Dictionary<string, double> { ["cruiseSet"] = set });
                _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"(Practice: the simulated truck's cruise is set at {set:0} km/h. It is in 7E0, 4100-41FF, and broadcast on HS-CAN.)"));
            }

            var typed = Ask(readings == 0
                ? $"{name} as shown now, or B to go back"
                : $"{name} as shown now — or R to rank the {readings} reading(s) you have");

            if (typed.Equals("B", StringComparison.OrdinalIgnoreCase) && readings == 0)
            {
                return;
            }

            if (typed.Equals("R", StringComparison.OrdinalIgnoreCase) || typed.Equals("B", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!TryShown(typed, out var shown, out var number))
            {
                _io.WriteLine("Type the number only, as shown — 100, or 13.4.");
                continue;
            }

            if (shown == 0)
            {
                _io.WriteLine("A zero matches almost everything — skipped. Type it when it reads something else.");
                continue;
            }

            // The same number again proves nothing new; only a changed one thins the list.
            if (seen.Contains(shown))
            {
                _io.WriteLine($"That is {number} again. Change it first — set a different speed — then type the new number.");
                continue;
            }

            var speed = await SpeedOnceAsync(ct).ConfigureAwait(false);
            var decimals = MatchRanker.Decimals(number);

            // The bus first: it takes seconds, and the number has just been read off the display.
            foreach (var place in places.Where(p => p.IsBroadcast))
            {
                _io.Write($"Listening to {Bus(place.Bus)}… ");
                var snapshot = await HearAsync(place.Bus, ct).ConfigureAwait(false);
                var hits = snapshot.Find(shown, decimals);
                place.Tally.Add(name, hits);
                _io.WriteLine($"{Count(snapshot.Frames.Count, "frame")} heard.");

                foreach (var id in hits.Select(h => h.Did).Distinct())
                {
                    capture.WriteLine(HuntOutput.Row(readings + 1, Where(place), name, number, FrameText(id), Convert.ToHexString(snapshot.Frames[id].Data), speed));
                }
            }

            foreach (var place in places.Where(p => !p.IsBroadcast))
            {
                _io.WriteLine($"Reading every identifier in {Hex(place.Module!.Value)} once…");
                if (!await place.Watch!.PassAsync(_session.RequestAsync, place.Bus, place.Module.Value, null, ct).ConfigureAwait(false))
                {
                    return;
                }

                var answers = Answers(place.Watch);
                var hits = MatchRanker.Find(answers, shown, decimals);
                place.Tally.Add(name, hits);

                foreach (var did in hits.Select(h => h.Did).Distinct())
                {
                    capture.WriteLine(HuntOutput.Row(readings + 1, Where(place), name, number, did.ToString("X4", CultureInfo.InvariantCulture), Convert.ToHexString(answers[did]), speed));
                }
            }

            readings++;
            seen.Add(shown);

            // Places, not decodes: one identifier read as unsigned and as signed is still one place.
            var fits = Candidates(places, name).Where(c => c.Matched == c.Rounds).Select(c => (c.Place, c.Hit.Did)).Distinct().Count();
            _io.WriteLine(readings == 1
                ? $"  {Count(fits, "place")} could hold {number}. Change it and type the new number."
                : fits switch
                {
                    0 => $"  Nothing fits all {readings} readings. It may be in another module or range, or one number was misread; R ranks the partial fits.",
                    1 => $"  1 place still fits all {readings} readings. That is probably it — R to rank and check it live.",
                    _ => $"  {fits} places still fit all {readings} readings. Another different number thins them further.",
                });
        }

        if (readings > 0)
        {
            await ReportPlacesAsync(target, places, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which modules to sweep and whether to listen, then the sweeps. Null or empty to go back.
    /// </summary>
    private async Task<List<MatchPlace>?> ChoosePlacesAsync(HuntTarget target, CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine($"{target.Name.ToUpperInvariant()} — where to look.");
        _io.WriteLine($"Needs: {target.Needs}");

        if (_placesSwept.TryGetValue(target.Id, out var held) &&
            Proceed($"Use the places from before ({string.Join(", ", held.Select(Where))})? Enter for yes, B to choose again"))
        {
            // Fresh places over the same sweeps: a new match starts with no readings of its own.
            return [.. held.Select(p => new MatchPlace(p.Bus, p.Module, p.Watch))];
        }

        if (!Proceed())
        {
            return null;
        }

        if (await ChooseModulesAsync(ct).ConfigureAwait(false) is not { } modules)
        {
            return null;
        }

        var listen = Ask("Listen to the bus as well, for a value that is broadcast rather than asked? [Y/n]");
        var buses = listen.StartsWith("N", StringComparison.OrdinalIgnoreCase) ? [] : _session.RequestBuses;

        if (modules.Count == 0 && buses.Count == 0)
        {
            _io.WriteLine("Nowhere to look: choose a module, or listen.");
            return null;
        }

        var places = new List<MatchPlace>([.. buses.Select(b => new MatchPlace(b, null, null))]);

        if (modules.Count > 0)
        {
            var ranges = AskRanges(target);
            if (!ConfirmSweep(ranges, modules.Count))
            {
                return null;
            }

            foreach (var (module, bus) in modules)
            {
                var found = await SweepRangesAsync(module, bus, ranges, ct).ConfigureAwait(false);
                var watch = new IdentifierWatch(found);
                if (watch.Items.Count == 0)
                {
                    _io.WriteLine($"  {Hex(module)}: nothing in those ranges gave a short answer worth watching, so it is left out.");
                    continue;
                }

                places.Add(new MatchPlace(bus, module, watch));
            }
        }

        if (places.Count == 0)
        {
            _io.WriteLine("Nothing to watch in those modules and ranges. Try other ranges, or listen.");
            return null;
        }

        var watched = places.Where(p => !p.IsBroadcast).Sum(p => p.Watch!.Items.Count);
        var pass = TimeSpan.FromSeconds(places.Where(p => !p.IsBroadcast).Sum(p => p.Watch!.PassTime().TotalSeconds)
            + (places.Count(p => p.IsBroadcast) * ListenSeconds));
        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Looking in {string.Join(", ", places.Select(Where))}: {watched} identifiers and the bus — a reading takes about {pass.TotalSeconds:0} s."));

        _placesSwept[target.Id] = places;
        return places;
    }

    /// <summary>
    /// Ask for modules: one, several separated by commas, ALL for every one S found, or NONE to only
    /// listen. Null to go back.
    /// </summary>
    private async Task<List<(ushort Module, CanBus Bus)>?> ChooseModulesAsync(CancellationToken ct)
    {
        if (_session.KnownModules.Count > 0)
        {
            _io.WriteLine("Modules that answered S:");
            foreach (var (address, bus) in _session.KnownModules.OrderBy(m => m.Key))
            {
                _io.WriteLine($"  {Hex(address)}  {Bus(bus),-22} {ModuleName(address) ?? ""}");
            }
        }
        else
        {
            _io.WriteLine("Not sure which module? ALL scans for every module first and searches each one.");
        }

        while (true)
        {
            var typed = Ask("Modules to search — 7E0, or several like 7E0, 720, or ALL, or NONE to only listen; B to go back");
            if (typed.Equals("B", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (typed.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (typed.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.KnownModules.Count == 0)
                {
                    await ScanModulesAsync(ct, confirm: false).ConfigureAwait(false);
                }

                if (_session.KnownModules.Count == 0)
                {
                    continue;
                }

                return [.. _session.KnownModules.OrderBy(m => m.Key).Select(m => (m.Key, m.Value))];
            }

            if (ParseModules(typed) is not { } addresses)
            {
                _io.WriteLine("Module addresses are three hex digits from 700 to 7F7, like 7E0, separated by commas.");
                continue;
            }

            var chosen = new List<(ushort, CanBus)>();
            foreach (var module in addresses)
            {
                if (await _session.FindModuleAsync(module, ct).ConfigureAwait(false) is { } bus)
                {
                    _io.WriteLine($"{Hex(module)} answers on {Bus(bus)}.");
                    chosen.Add((module, bus));
                }
                else
                {
                    _io.WriteLine($"{Hex(module)} did not answer on either bus — left out.");
                }
            }

            if (chosen.Count > 0)
            {
                return chosen;
            }
        }
    }

    /// <summary>Module addresses separated by commas or spaces, each a valid one, without repeats.</summary>
    internal static List<ushort>? ParseModules(string text)
    {
        var modules = new List<ushort>();
        foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ushort.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var module) ||
                !PidRequest.IsModuleAddress(module))
            {
                return null;
            }

            if (!modules.Contains(module))
            {
                modules.Add(module);
            }
        }

        return modules.Count > 0 ? modules : null;
    }

    /// <summary>Listen to one bus for <see cref="ListenSeconds"/> and gather what it said.</summary>
    private async Task<BroadcastSnapshot> HearAsync(CanBus bus, CancellationToken ct)
    {
        var heard = new List<(uint, byte[])>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(ListenSeconds));

        try
        {
            await foreach (var frame in _session.Monitor.ListenAsync(bus, null, window.Token, Rate(bus)).ConfigureAwait(false))
            {
                heard.Add((frame.Id, frame.Data));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The window closing is how a listen ends.
        }

        await _session.RestoreAsync(ct).ConfigureAwait(false);
        return BroadcastSnapshot.From(heard);
    }

    /// <summary>Every place's candidates, in one list, each with where it was found.</summary>
    private static List<(MatchPlace Place, MatchHit Hit, double Score, int Matched, int Rounds)> Candidates(IEnumerable<MatchPlace> places, string name) =>
        [
            .. places
                .SelectMany(place => place.Tally.Ranked(name).Select((r, order) => (place, r.Hit, r.Score, r.Matched, r.Rounds, order)))
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.order)
                .Select(c => (c.place, c.Hit, c.Score, c.Matched, c.Rounds)),
        ];

    /// <summary>One table across every place, the live check for whichever is picked, and the findings.</summary>
    private async Task ReportPlacesAsync(HuntTarget target, IReadOnlyList<MatchPlace> places, CancellationToken ct)
    {
        // One row a place: an identifier that fits read as unsigned, signed or one byte of it is still
        // one place, and the best-ranked way of reading it is the one shown and checked live.
        var ranked = Candidates(places, target.Name).DistinctBy(c => (Where(c.Place), c.Hit.Did)).Take(8).ToList();
        if (ranked.Count == 0)
        {
            _io.WriteLine("Nothing held those numbers. Try other modules or ranges, or listen.");
            _status[target.Id] = "nothing yet";
            return;
        }

        _io.WriteLine();
        _io.WriteLine($"{target.Name} — likeliest first, wherever it was found:");
        _io.WriteLine("  #   WHERE                    ID     READ     DECODE                  GIVES     MATCHED");
        for (var i = 0; i < ranked.Count; i++)
        {
            var (place, hit, _, matched, rounds) = ranked[i];
            _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1,-3} {Where(place),-24} {Id(place, hit.Did),-6} {hit.Reading,-7}  {hit.Transform,-22}  {hit.Decoded,-8:0.##}  {matched}/{rounds}"));
        }

        var verdicts = new Dictionary<int, string>();
        while (true)
        {
            _io.WriteLine();
            var pick = Ask("Number to read it live (change the number and watch it follow), Enter when finished");
            if (pick.Length == 0)
            {
                break;
            }

            if (!int.TryParse(pick, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > ranked.Count)
            {
                continue;
            }

            var (place, hit, _, _, _) = ranked[n - 1];
            if (place.IsBroadcast)
            {
                await LiveFrameAsync(place.Bus, hit, ct).ConfigureAwait(false);
            }
            else
            {
                await LiveRequestAsync(hit.Did, hit.Reading, raw => hit.Transform.Apply(raw), place.Module!.Value, place.Bus, null, "", ct).ConfigureAwait(false);
            }

            verdicts[n - 1] = Verdict();
            if (verdicts[n - 1] == "confirmed")
            {
                break;
            }
        }

        for (var i = 0; i < ranked.Count; i++)
        {
            var (place, hit, score, matched, rounds) = ranked[i];
            var evidence = string.Create(CultureInfo.InvariantCulture, $"raw {hit.Raw} gives {hit.Decoded:0.###}; matched {matched} of {rounds}");
            _output.Finding(_output.Now(), target.Name, target.Signal, "match", Bus(place.Bus),
                place.IsBroadcast ? "" : Hex(place.Module!.Value),
                place.IsBroadcast ? FrameText(hit.Did) : "",
                place.IsBroadcast ? "" : hit.DidText,
                target.Name, hit.Reading.ToString(), hit.Transform.ToString(), evidence,
                Math.Round(score, 3), verdicts.GetValueOrDefault(i, "unchecked"), place.IsBroadcast ? "broadcast" : "");
        }

        _status[target.Id] = verdicts.ContainsValue("confirmed") ? "CONFIRMED (see findings)" : $"{ranked.Count} candidate(s)";
    }

    /// <summary>Hear one frame alone and print the field decoded each time it changes, until Enter.</summary>
    private async Task LiveFrameAsync(CanBus bus, MatchHit hit, CancellationToken ct)
    {
        _io.WriteLine($"LIVE: frame {FrameText(hit.Did)} on {Bus(bus)} read {hit.Reading} {hit.Transform}. Change the number. Press Enter to stop.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listening = Task.Run(async () =>
        {
            string? last = null;
            try
            {
                await foreach (var frame in _session.Monitor.ListenAsync(bus, hit.Did, stop.Token, Rate(bus)).ConfigureAwait(false))
                {
                    var raw = hit.Reading.Read(frame.Data);
                    var shown = raw is { } r ? hit.Transform.Apply(r).ToString("0.#", CultureInfo.InvariantCulture) : "—";
                    if (shown != last)
                    {
                        last = shown;
                        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"  {frame.At.TotalSeconds,6:0.0}s   raw {raw?.ToString(CultureInfo.InvariantCulture) ?? "—",-8} decoded {shown}"));
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        await _io.ReadLineAsync(ct).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await listening.ConfigureAwait(false);
        await _session.RestoreAsync(ct).ConfigureAwait(false);
    }

    private static string Where(MatchPlace place) =>
        place.IsBroadcast ? $"broadcast on {ShortBus(place.Bus)}" : $"{Hex(place.Module!.Value)} on {ShortBus(place.Bus)}";

    /// <summary>A bus in a table column: the full name says its rate, which a column has no room for.</summary>
    private static string ShortBus(CanBus bus) => bus == CanBus.Hs ? "HS-CAN" : "pins 3/11";

    private static string Id(MatchPlace place, ushort id) =>
        place.IsBroadcast ? FrameText(id) : id.ToString("X4", CultureInfo.InvariantCulture);

    private static string FrameText(ushort id) => id.ToString("X3", CultureInfo.InvariantCulture);

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");

    /// <summary>The truck's speed, asked up to three times since one unanswered request is routine.</summary>
    private async Task<double?> SpeedOnceAsync(CancellationToken ct)
    {
        double? speed = null;
        for (var attempt = 0; attempt < 3 && speed is null; attempt++)
        {
            speed = await _session.SpeedAsync(ct).ConfigureAwait(false);
        }

        return speed;
    }

    /// <summary>The number a person typed, without the unit they may have typed after it.</summary>
    internal static bool TryShown(string typed, out double shown, out string number)
    {
        number = new string(typed.Trim().TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out shown);
    }

    /// <summary>A name as a file-safe id: lower case, words joined by hyphens.</summary>
    internal static string Slug(string name)
    {
        var cleaned = new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ')]);
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "value" : string.Join('-', words);
    }
}
