using System.Globalization;
using DashDeck.Core.Discovery;
using DashDeck.Core.Discovery.Hunt;

namespace DashDeck.IdHunter;

/// <summary>
/// M on the main menu: match a number named on the spot, read as many times as it changes
/// (ADR-0069).
/// </summary>
/// <remarks>
/// The checklist's match targets each name one thing and a fixed number of rounds. This is the same
/// engine with the target made up at the menu — cruise control's set speed, say — and the rounds left
/// open, because the person knows better than a file how many different values they can produce.
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

    private async Task FreeMatchAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("MATCH A NUMBER YOU NAME — sweep a module once, then type the number each time it changes.");
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

        if (await PrepareAsync(target, ct).ConfigureAwait(false) is not { } prepared)
        {
            return;
        }

        var (module, bus, watch) = prepared;
        var tally = new MatchTally();

        using var capture = _output.Capture($"match-{target.Id}-{Hex(module)}-{_clock.UtcNow.ToLocalTime():HHmmss}", "round,label,shown,did,data,km_h");

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
                    $"(Practice: the simulated truck's cruise is set at {set:0} km/h. Name it Cruise set speed and look in 7E0, 4100-41FF.)"));
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

            _io.WriteLine("Reading every identifier once…");
            if (!await watch.PassAsync(_session.RequestAsync, bus, module, null, ct).ConfigureAwait(false))
            {
                return;
            }

            readings++;
            seen.Add(shown);
            var answers = Answers(watch);
            var hits = MatchRanker.Find(answers, shown, MatchRanker.Decimals(number));
            tally.Add(name, hits);

            foreach (var did in hits.Select(h => h.Did).Distinct())
            {
                capture.WriteLine(HuntOutput.Row(readings, name, number, did.ToString("X4", CultureInfo.InvariantCulture), Convert.ToHexString(answers[did]), speed));
            }

            // Identifiers, not decodes: one identifier read as unsigned and as signed is still one place.
            var candidates = tally.Ranked(name).Where(r => r.Matched == r.Rounds).Select(r => r.Hit.Did).Distinct().Count();
            _io.WriteLine(readings == 1
                ? $"  {Count(hits.Select(h => h.Did).Distinct().Count(), "identifier")} could hold {number}. Change it and type the new number."
                : candidates switch
                {
                    0 => $"  Nothing fits all {readings} readings. It may be in another module or range, or one number was misread; R ranks the partial fits.",
                    1 => $"  1 still fits all {readings} readings. That is probably it — R to rank and check it live.",
                    _ => $"  {candidates} still fit all {readings} readings. Another different number thins them further.",
                });
        }

        if (readings == 0)
        {
            return;
        }

        await ReportMatchAsync(target, tally, module, bus, ct).ConfigureAwait(false);
    }

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
