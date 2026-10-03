using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.IdHunter;

/// <summary>
/// Ask a module while you do the steps: for a value that is never broadcast, only held by the module
/// that runs it — the seat climate module's levels, say (ADR-0044).
/// </summary>
/// <remarks>
/// The module's identifiers are swept, then asked again and again through the same steps a listen
/// uses. Each answer is ranked exactly as a heard frame would be — by <see cref="BroadcastRanker"/>,
/// with the identifier in the place of the frame's — so the same rules keep chance out.
/// </remarks>
internal sealed partial class Wizard
{
    /// <summary>A free-form ask: name what you will do, and the module to ask.</summary>
    private async Task FreeAskStepsAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("ASK A MODULE WHILE YOU DO SOMETHING — for values the module keeps to itself.");
        var name = Ask("What will you do? (e.g. 'driver seat cooling on'), or B to go back");
        if (name.Length == 0 || name.Equals("B", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        HuntStep Step(int state) => new()
        {
            Label = state == 0 ? "BEFORE" : "DONE",
            State = state,
            Do = state == 0 ? $"Undo it: '{name}' OFF / as it was." : $"Do it: '{name}'.",
        };

        var target = new HuntTarget
        {
            Id = "ask-" + new string(name.Where(char.IsLetterOrDigit).Take(20).ToArray()),
            Name = name,
            Method = "listen",
            Hold = 5,
        };

        await AskStepsAsync(target, [Step(0), Step(1), Step(0), Step(1), Step(0)], ct).ConfigureAwait(false);
    }

    private async Task AskStepsAsync(HuntTarget target, IReadOnlyList<HuntStep> steps, CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine($"{target.Name.ToUpperInvariant()} — asking a module while you do it.");

        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        if (await ChooseModuleAsync(target, ct).ConfigureAwait(false) is not { } chosen)
        {
            return;
        }

        var (module, bus) = chosen;
        var sweepTarget = target with { Ranges = target.Ranges.Count > 0 ? target.Ranges : ["0000-0FFF"] };
        if (await SweepAsync(sweepTarget, module, bus, ct).ConfigureAwait(false) is not { } found)
        {
            return;
        }

        var watch = new IdentifierWatch(found);
        if (watch.Items.Count == 0)
        {
            _io.WriteLine("Nothing in those ranges gave a short answer to watch. Try other ranges.");
            return;
        }

        // Every step is held long enough for at least two passes over everything found.
        var pass = watch.PassTime();
        var hold = Math.Max(Hold(target), (int)Math.Ceiling(pass.TotalSeconds * 2.2));
        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{watch.Items.Count} identifiers — one pass takes about {pass.TotalSeconds:0} s, so each step is held {hold} s."));
        _io.WriteLine($"You will do {steps.Count} steps. After each, press Enter and hold still.");
        if (!Proceed())
        {
            return;
        }

        var frames = new List<CanFrame>();
        var phases = new List<HuntPhase>();
        var started = _clock.UtcNow;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            _io.WriteLine();
            _io.WriteLine($"STEP {i + 1} of {steps.Count}: {step.Do}");
            var answer = (_io.ReadLine() ?? "q").Trim().ToUpperInvariant();
            if (answer is "Q" or "B")
            {
                _io.WriteLine("Stopped; nothing ranked.");
                return;
            }

            _session.Simulate(step.Simulate);
            var start = _clock.UtcNow - started;
            _io.Write("  Hold still while it asks… ");

            while ((_clock.UtcNow - started - start).TotalSeconds < hold)
            {
                if (!await watch.PassAsync(_session.RequestAsync, bus, module, null, ct).ConfigureAwait(false))
                {
                    return;
                }

                var at = _clock.UtcNow - started;
                foreach (var item in watch.Items.Where(x => x.Current is not null && !x.Missed))
                {
                    frames.Add(new CanFrame(at, bus, item.Did, item.Current!));
                }

                _io.Write(".");
            }

            _io.WriteLine(" got it.");
            phases.Add(new HuntPhase(step.Label, step.State, start, _clock.UtcNow - started));
        }

        using (var capture = _output.Capture($"ask-{target.Id}-{Hex(module)}-{_clock.UtcNow.ToLocalTime():HHmmss}", "seconds,step,did,data"))
        {
            foreach (var frame in frames)
            {
                var phase = phases.FindIndex(p => frame.At >= p.Start && frame.At <= p.End);
                capture.WriteLine(HuntOutput.Row(frame.At.TotalSeconds, phase < 0 ? "" : phases[phase].Label,
                    frame.Id.ToString("X4", CultureInfo.InvariantCulture), Convert.ToHexString(frame.Data)));
            }
        }

        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var ranked = BroadcastRanker.Rank(frames, phases).Take(10).ToList();
        if (ranked.Count == 0)
        {
            _io.WriteLine($"Nothing {Hex(module)} answered in those ranges followed the steps. Try other ranges.");
            _status[target.Id] = "nothing yet";
            return;
        }

        _io.WriteLine();
        _io.WriteLine($"What {Hex(module)} changed with you, likeliest first:");
        _io.WriteLine("  #  ID     FIELD                 EACH STEP" + new string(' ', 14) + "SCORE  TELLS APART");
        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            var apart = c.SeparatesAll ? "every step" : $"{c.Separated} of {c.Pairs} pairs";
            _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1}  {c.Id:X4}   {c.Field,-20}  {string.Join(' ', c.Values),-22}  {c.Score:0.00}   {apart}"));
        }

        var verdicts = new Dictionary<int, string>();
        while (true)
        {
            _io.WriteLine();
            var pick = Ask("Number to read it live (do it again and watch it change), Enter when finished");
            if (pick.Length == 0)
            {
                break;
            }

            if (!int.TryParse(pick, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > ranked.Count)
            {
                continue;
            }

            var c = ranked[n - 1];
            _io.WriteLine($"LIVE: {Hex(module)} {c.Id:X4} {c.Field}. Do it again a few times, slowly. Press Enter to stop.");

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var enter = _io.ReadLineAsync(stop.Token);
            int? last = null;
            var step = 1;

            while (!enter.IsCompleted)
            {
                var response = await _session.RequestAsync(new PidRequest(0x22, (ushort)c.Id, bus, module), stop.Token).ConfigureAwait(false);
                var value = response.IsSuccess ? c.Read(response.Data) : null;
                if (value != last)
                {
                    last = value;
                    var label = value is { } v && c.Values.ToList().IndexOf(v) is var k and >= 0 && k < steps.Count ? steps[k].Label : "";
                    _io.WriteLine($"  {value?.ToString(CultureInfo.InvariantCulture) ?? "—"}   {label}");
                }

                if (_session.Simulated)
                {
                    _session.Simulate(steps[step++ % steps.Count].Simulate);
                }

                try
                {
                    await Task.Delay(400, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await stop.CancelAsync().ConfigureAwait(false);
            _session.Simulate(steps[0].Simulate);
            verdicts[n - 1] = Verdict();
            if (verdicts[n - 1] == "confirmed")
            {
                break;
            }
        }

        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            var evidence = string.Join(' ', steps.Take(c.Values.Count).Select((s, k) => $"{s.Label}={c.Values[k]}"));
            _output.Finding(_output.Now(), target.Name, target.Signal, "ask", Bus(bus), Hex(module), "",
                c.Id.ToString("X4", CultureInfo.InvariantCulture), c.Field, $"mask {c.Mask:X2}", "", evidence, c.Score,
                verdicts.GetValueOrDefault(i, "unchecked"), c.SeparatesAll ? "" : $"tells {c.Separated} of {c.Pairs} state pairs apart");
        }

        _status[target.Id] = verdicts.ContainsValue("confirmed")
            ? $"CONFIRMED {Hex(module)} {ranked[verdicts.First(v => v.Value == "confirmed").Key].Id:X4}"
            : $"{ranked.Count} candidate(s)";
    }
}
