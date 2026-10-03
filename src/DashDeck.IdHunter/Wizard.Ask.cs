using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle;

namespace DashDeck.IdHunter;

/// <summary>Follow and match: sweep a module, then watch its identifiers against a reading.</summary>
internal sealed partial class Wizard
{
    /// <summary>A module's swept identifiers, kept between rounds and visits.</summary>
    private readonly Dictionary<string, (ushort Module, CanBus Bus, List<FoundIdentifier> Found)> _swept = [];

    private async Task<(ushort Module, CanBus Bus, IdentifierWatch Watch)?> PrepareAsync(HuntTarget target, CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine($"{target.Name.ToUpperInvariant()} — asking a module for its identifiers.");
        _io.WriteLine($"Needs: {target.Needs}");

        if (_swept.TryGetValue(target.Id, out var held) &&
            Proceed($"Use the sweep of {Hex(held.Module)} from before ({held.Found.Count} identifiers)? Enter for yes, B to sweep again"))
        {
            return (held.Module, held.Bus, new IdentifierWatch(held.Found));
        }

        if (!Proceed() || !await ParkedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        if (await ChooseModuleAsync(target, ct).ConfigureAwait(false) is not { } chosen)
        {
            return null;
        }

        if (await SweepAsync(target, chosen.Module, chosen.Bus, ct).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var watch = new IdentifierWatch(found);
        if (watch.Items.Count == 0)
        {
            _io.WriteLine("Nothing in those ranges gave a short answer worth watching. Try other ranges or another module.");
            return null;
        }

        _swept[target.Id] = (chosen.Module, chosen.Bus, found);
        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{watch.Items.Count} identifiers to watch — one pass takes about {watch.PassTime().TotalSeconds:0} s."));
        return (chosen.Module, chosen.Bus, watch);
    }

    private static Dictionary<ushort, byte[]> Answers(IdentifierWatch watch) =>
        watch.Items.Where(i => i.Current is not null && !i.Missed).ToDictionary(i => i.Did, i => i.Current!);

    // ── Follow ───────────────────────────────────────────────────────────────

    private async Task FollowAsync(HuntTarget target, CancellationToken ct)
    {
        if (await PrepareAsync(target, ct).ConfigureAwait(false) is not { } prepared)
        {
            return;
        }

        var (module, bus, watch) = prepared;
        var reference = target.Reference == "rpm" ? "rpm" : "coolant";
        var unit = reference == "rpm" ? "rpm" : "°C";
        var minutes = FollowMinutes ?? target.Minutes;

        _io.WriteLine();
        _io.WriteLine(target.Instructions);
        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"The guide watches for up to {minutes:0.#} minutes, comparing against {reference}. Press Enter at any time to stop early."));
        if (!Proceed("Press Enter to start watching, or B to go back"))
        {
            return;
        }

        var passes = new List<FollowPass>();
        var started = _clock.UtcNow;
        var lastBlip = TimeSpan.Zero;
        var blipping = false;

        using var capture = _output.Capture($"follow-{target.Id}-{Hex(module)}-{_clock.UtcNow.ToLocalTime():HHmmss}",
            "seconds,reference," + string.Join(',', watch.Items.Select(i => i.Did.ToString("X4", CultureInfo.InvariantCulture))));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enter = _io.ReadLineAsync(stop.Token);

        while (!enter.IsCompleted && _clock.UtcNow - started < TimeSpan.FromMinutes(minutes))
        {
            var elapsed = _clock.UtcNow - started;

            if (target.Blips)
            {
                if (!blipping && elapsed - lastBlip > TimeSpan.FromSeconds(15))
                {
                    _io.WriteLine(">>> REV now: gently to about 2,000 rpm, hold two seconds, let it fall back.");
                    blipping = true;
                    lastBlip = elapsed;
                    _session.Simulate(new Dictionary<string, double> { ["rev"] = 1300 });
                }
                else if (blipping && elapsed - lastBlip > TimeSpan.FromSeconds(4))
                {
                    _io.WriteLine(">>> back to idle.");
                    blipping = false;
                    _session.Simulate(new Dictionary<string, double> { ["rev"] = 0 });
                }
            }

            if (!await watch.PassAsync(_session.RequestAsync, bus, module, null, stop.Token).ConfigureAwait(false))
            {
                break;
            }

            var value = await _session.ReferenceAsync(reference, stop.Token).ConfigureAwait(false);
            var speed = await _session.SpeedAsync(stop.Token).ConfigureAwait(false);
            if (speed is > 0)
            {
                _io.WriteLine("The truck is moving — stopped. Park, then start again.");
                break;
            }

            var answers = Answers(watch);
            passes.Add(new FollowPass(_clock.UtcNow - started, value, answers));
            capture.WriteLine(HuntOutput.Row([
                (_clock.UtcNow - started).TotalSeconds, value,
                .. watch.Items.Select(i => i.Current is { } c && !i.Missed ? Convert.ToHexString(c) : ""),
            ]));

            var leader = passes.Count >= FollowRanker.MinimumPoints ? FollowRanker.Rank(passes).FirstOrDefault() : null;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"  pass {passes.Count,3}  {elapsed:m\\:ss}  {reference} {value?.ToString("0", CultureInfo.InvariantCulture) ?? "—"} {unit}");
            if (leader is not null)
            {
                line += string.Create(CultureInfo.InvariantCulture, $"   closest so far: {leader.DidText} (r = {leader.Correlation:0.00})");
            }

            _io.WriteLine(line);
        }

        await stop.CancelAsync().ConfigureAwait(false);
        _session.Simulate(new Dictionary<string, double> { ["rev"] = 0 });

        var ranked = FollowRanker.Rank(passes).Take(10).ToList();
        if (ranked.Count == 0)
        {
            _io.WriteLine(passes.Count < FollowRanker.MinimumPoints
                ? $"Only {passes.Count} passes — at least {FollowRanker.MinimumPoints} are needed. Watch for longer."
                : $"Nothing moved while {reference} did. Did {reference} change? Try another module or range.");
            _status[target.Id] = "nothing yet";
            return;
        }

        _io.WriteLine();
        _io.WriteLine($"What moved with {reference}, closest first. r near 1 rose with it, near −1 fell as it rose:");
        _io.WriteLine($"  #  ID     READ     r       {reference.ToUpperInvariant()} ≈ a × value + b      LOOKS LIKE");
        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1,-2} {c.DidText}   {c.Reading,-7}  {c.Correlation,5:0.00}   a={c.Slope,-9:0.####} b={c.Intercept,-8:0.#}  {c.Hint ?? ""}"));
        }

        var verdicts = await LiveAskAsync(ranked.Select(c => (c.Did, c.Reading, Fit: (Func<long, double>)(raw => (c.Slope * raw) + c.Intercept))).ToList(),
            module, bus, reference, unit, ct).ConfigureAwait(false);

        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            _output.Finding(_output.Now(), target.Name, target.Signal, "follow", Bus(bus), Hex(module), "", c.DidText, "", c.Reading.ToString(),
                c.Hint ?? "", string.Create(CultureInfo.InvariantCulture,
                    $"r={c.Correlation:0.000} vs {reference}; {reference}≈{c.Slope:0.#####}×raw+{c.Intercept:0.##}; raw {c.Low}..{c.High}; {c.Points} passes"),
                Math.Round(Math.Abs(c.Correlation), 3), verdicts.GetValueOrDefault(i, "unchecked"), "");
        }

        _status[target.Id] = verdicts.ContainsValue("confirmed")
            ? $"CONFIRMED {Hex(module)} {ranked[verdicts.First(v => v.Value == "confirmed").Key].DidText}"
            : $"{ranked.Count} candidate(s)";
    }

    /// <summary>
    /// Pick a candidate and read it over and over beside the reference, so the person can see whether
    /// it tracks — and compare it with FORScan or the cluster if they have them.
    /// </summary>
    private async Task<Dictionary<int, string>> LiveAskAsync(
        IReadOnlyList<(ushort Did, Reading Reading, Func<long, double> Decode)> candidates,
        ushort module, CanBus bus, string? reference, string unit, CancellationToken ct)
    {
        var verdicts = new Dictionary<int, string>();

        while (true)
        {
            _io.WriteLine();
            var pick = Ask("Number to read it live (compare with FORScan or the cluster), Enter when finished");
            if (pick.Length == 0)
            {
                return verdicts;
            }

            if (!int.TryParse(pick, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > candidates.Count)
            {
                continue;
            }

            var (did, reading, decode) = candidates[n - 1];
            _io.WriteLine($"LIVE: {Hex(module)} {did:X4} read {reading}. Change what it should follow. Press Enter to stop.");

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var enter = _io.ReadLineAsync(stop.Token);

            while (!enter.IsCompleted)
            {
                var response = await _session.RequestAsync(new PidRequest(0x22, did, bus, module), stop.Token).ConfigureAwait(false);
                var refValue = reference is null ? null : await _session.ReferenceAsync(reference, stop.Token).ConfigureAwait(false);
                var raw = response.IsSuccess ? reading.Read(response.Data) : null;

                var decoded = raw is { } r ? decode(r).ToString("0.#", CultureInfo.InvariantCulture) : "—";
                var line = $"  raw {raw?.ToString(CultureInfo.InvariantCulture) ?? "—",-8} decoded {decoded,-8}";
                if (reference is not null)
                {
                    line += $" {reference} {refValue?.ToString("0", CultureInfo.InvariantCulture) ?? "—"} {unit}";
                }

                _io.WriteLine(line);

                try
                {
                    await Task.Delay(700, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await stop.CancelAsync().ConfigureAwait(false);
            verdicts[n - 1] = Verdict();
            if (verdicts[n - 1] == "confirmed")
            {
                return verdicts;
            }
        }
    }

    // ── Match ────────────────────────────────────────────────────────────────

    private async Task MatchAsync(HuntTarget target, CancellationToken ct)
    {
        if (await PrepareAsync(target, ct).ConfigureAwait(false) is not { } prepared)
        {
            return;
        }

        var (module, bus, watch) = prepared;
        var tally = new MatchTally();

        using var capture = _output.Capture($"match-{target.Id}-{Hex(module)}-{_clock.UtcNow.ToLocalTime():HHmmss}", "round,label,shown,did,data");

        for (var round = 1; round <= Math.Max(1, target.Rounds); round++)
        {
            if (round > 1)
            {
                _io.WriteLine();
                _io.WriteLine($"ROUND {round} of {target.Rounds}. {target.Between}");
                if (!Proceed("Press Enter when the number has changed, or B to stop with what you have"))
                {
                    break;
                }
            }

            _io.WriteLine("Reading every identifier once…");
            if (!await watch.PassAsync(_session.RequestAsync, bus, module, null, ct).ConfigureAwait(false))
            {
                return;
            }

            var answers = Answers(watch);

            foreach (var reading in target.Readings)
            {
                var hint = _session.SimulatedReading(reading.Simulate) is { } simulated
                    ? string.Create(CultureInfo.InvariantCulture, $" (the simulated cluster shows {simulated})")
                    : "";

                double shown;
                string typed;
                while (true)
                {
                    typed = Ask($"{reading.Ask}{hint}");
                    var number = new string(typed.TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray());
                    if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out shown))
                    {
                        typed = number;
                        break;
                    }

                    _io.WriteLine("Type the number only, as shown — 412, or 13.4.");
                }

                if (shown == 0)
                {
                    _io.WriteLine("A zero matches almost everything — skipped. Come back when it reads something else.");
                    continue;
                }

                var hits = MatchRanker.Find(answers, shown, MatchRanker.Decimals(typed));
                tally.Add(reading.Label, hits);

                foreach (var did in hits.Select(h => h.Did).Distinct())
                {
                    capture.WriteLine(HuntOutput.Row(round, reading.Label, typed, did.ToString("X4", CultureInfo.InvariantCulture), Convert.ToHexString(answers[did])));
                }

                _io.WriteLine($"  {hits.Select(h => h.Did).Distinct().Count()} identifiers could hold {typed}.");
            }
        }

        var shownAny = false;
        var all = new List<(string Label, MatchHit Hit, double Score, int Matched, int Rounds)>();

        foreach (var label in tally.Labels)
        {
            var ranked = tally.Ranked(label).Take(6).ToList();
            if (ranked.Count == 0)
            {
                continue;
            }

            shownAny = true;
            _io.WriteLine();
            _io.WriteLine($"{label} — likeliest first:");
            _io.WriteLine("  #   ID     READ     DECODE                  GIVES     MATCHED");
            foreach (var r in ranked)
            {
                all.Add((label, r.Hit, r.Score, r.Matched, r.Rounds));
                _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {all.Count,-3} {r.Hit.DidText}   {r.Hit.Reading,-7}  {r.Hit.Transform,-22}  {r.Hit.Decoded,-8:0.##}  {r.Matched}/{r.Rounds}{(r.Score > 1 ? "  shared with the others" : "")}"));
            }
        }

        if (!shownAny)
        {
            _io.WriteLine("Nothing held those numbers. Try another module or range.");
            _status[target.Id] = "nothing yet";
            return;
        }

        if (all.All(a => a.Rounds < 2) && target.Rounds < 2 && target.Readings.Count < 2)
        {
            _io.WriteLine("One reading matches a lot by chance. Run it again later, when the number has changed, to thin these out.");
        }

        var verdicts = await LiveAskAsync(all.Select(a => (a.Hit.Did, a.Hit.Reading, Fit: (Func<long, double>)(raw => a.Hit.Transform.Apply(raw)))).ToList(),
            module, bus, null, "", ct).ConfigureAwait(false);

        for (var i = 0; i < all.Count; i++)
        {
            var a = all[i];
            _output.Finding(_output.Now(), target.Name, target.Signal, "match", Bus(bus), Hex(module), "", a.Hit.DidText, a.Label, a.Hit.Reading.ToString(),
                a.Hit.Transform.ToString(), string.Create(CultureInfo.InvariantCulture, $"raw {a.Hit.Raw} gives {a.Hit.Decoded:0.###}; matched {a.Matched} of {a.Rounds}"),
                Math.Round(a.Score, 3), verdicts.GetValueOrDefault(i, "unchecked"), "");
        }

        _status[target.Id] = verdicts.ContainsValue("confirmed") ? "CONFIRMED (see findings)" : $"{all.Count} candidate(s)";
    }
}
