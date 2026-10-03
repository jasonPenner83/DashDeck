using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.IdHunter;

/// <summary>Listen: hold the truck in each state while the bus is heard, then rank what followed.</summary>
internal sealed partial class Wizard
{
    private async Task ListenAsync(HuntTarget target, CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine($"{target.Name.ToUpperInvariant()} — listening to the bus while you do it.");
        _io.WriteLine($"Needs: {target.Needs}");
        _io.WriteLine($"You will do {target.Steps.Count} steps. After each, press Enter and hold still for {Hold(target)} seconds.");
        if (!Proceed() || !await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var buses = target.Buses
            .Select(name => name.Equals("hs", StringComparison.OrdinalIgnoreCase) ? CanBus.Hs : CanBus.Ms)
            .Where(bus => bus == CanBus.Hs || _session.Pins311BitRate is not null)
            .Distinct()
            .ToList();

        if (buses.Count < target.Buses.Count)
        {
            _io.WriteLine("Pins 3/11 were silent at start-up, so this listens on HS-CAN only.");
        }

        foreach (var bus in buses)
        {
            var outcome = await ListenOnAsync(target, target.Steps, bus, ct).ConfigureAwait(false);

            if (outcome is "confirmed" or "stopped")
            {
                return;
            }

            if (bus != buses[^1] &&
                !Proceed($"Try {Bus(buses[^1])} too? Press Enter for yes, B for no"))
            {
                return;
            }
        }
    }

    private int? Rate(CanBus bus) => bus == CanBus.Ms ? _session.Pins311BitRate : null;

    private int Hold(HuntTarget target) => HoldSeconds ?? Math.Max(2, target.Hold);

    /// <summary>A listen the person names: what they will do, and what it looks like undone.</summary>
    private async Task FreeListenAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("LISTEN FREELY — you name an action; the guide asks you to do it and undo it twice.");
        var name = Ask("What will you do? (e.g. 'hazard lights on'), or B to go back");
        if (name.Length == 0 || name.Equals("B", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var bus = CanBus.Hs;
        if (_session.Pins311BitRate is not null)
        {
            var busName = Ask($"Which bus — 1 for {Bus(CanBus.Ms)}, 2 for HS-CAN [1]");
            bus = busName == "2" ? CanBus.Hs : CanBus.Ms;
        }

        var target = new HuntTarget { Id = "free-" + new string(name.Where(char.IsLetterOrDigit).Take(20).ToArray()), Name = name, Method = "listen", Hold = 5 };
        HuntStep Step(int state) => new()
        {
            Label = state == 0 ? "BEFORE" : "DONE",
            State = state,
            Do = state == 0 ? $"Undo it: '{name}' OFF / as it was." : $"Do it: '{name}'.",
        };

        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        await ListenOnAsync(target, [Step(0), Step(1), Step(0), Step(1), Step(0)], bus, ct).ConfigureAwait(false);
    }

    /// <returns><c>confirmed</c>, <c>stopped</c>, or anything else to carry on.</returns>
    private async Task<string> ListenOnAsync(HuntTarget target, IReadOnlyList<HuntStep> steps, CanBus bus, CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine($"Listening on {Bus(bus)}…");

        var frames = new List<CanFrame>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listening = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in _session.Monitor.ListenAsync(bus, null, stop.Token, Rate(bus)).ConfigureAwait(false))
                {
                    lock (frames)
                    {
                        frames.Add(frame);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped while still setting up: nothing to keep.
            }
        }, CancellationToken.None);

        await Task.Delay(1500, ct).ConfigureAwait(false);
        int heard;
        lock (frames)
        {
            heard = frames.Count;
        }

        if (heard == 0)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await listening.ConfigureAwait(false);
            await _session.RestoreAsync(ct).ConfigureAwait(false);
            _io.WriteLine($"Nothing heard on {Bus(bus)}{(_session.Monitor.Problem is { } p ? $": {p}" : "")}. Is the ignition on?");
            return "nothing";
        }

        _io.WriteLine($"Hearing {Bus(bus)}.");

        var phases = new List<HuntPhase>();
        var stopped = false;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            _io.WriteLine();
            _io.WriteLine($"STEP {i + 1} of {steps.Count}: {step.Do}");
            var answer = (await _io.ReadLineAsync(ct).ConfigureAwait(false) ?? "q").Trim().ToUpperInvariant();
            if (answer is "Q" or "B")
            {
                stopped = true;
                break;
            }

            _session.Simulate(step.Simulate);
            var start = _clock.UtcNow - _session.Monitor.StartedAt;

            _io.Write("  Hold still… ");
            for (var s = Hold(target); s > 0; s--)
            {
                _io.Write($"{s} ");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            _io.WriteLine("got it.");
            phases.Add(new HuntPhase(step.Label, step.State, start, _clock.UtcNow - _session.Monitor.StartedAt));
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await listening.ConfigureAwait(false);
        var bursts = _session.Monitor.Restarts;
        await _session.RestoreAsync(ct).ConfigureAwait(false);

        List<CanFrame> heardFrames;
        lock (frames)
        {
            heardFrames = [.. frames];
        }

        var captureName = $"listen-{target.Id}-{(bus == CanBus.Ms ? "ms" : "hs")}-{_clock.UtcNow.ToLocalTime():HHmmss}";
        using (var capture = _output.Capture(captureName, "seconds,step,id,data"))
        {
            foreach (var frame in heardFrames)
            {
                var phase = phases.FindIndex(p => frame.At >= p.Start && frame.At <= p.End);
                capture.WriteLine(HuntOutput.Row(frame.At.TotalSeconds, phase < 0 ? "" : phases[phase].Label, frame.IdText, frame.DataText));
            }
        }

        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Heard {heardFrames.Count} frames from {heardFrames.Select(f => f.Id).Distinct().Count()} identifiers."));
        if (bursts > 0)
        {
            _io.WriteLine($"The bus is busier than the adapter's link, so it was heard in {bursts + 1} bursts rather than continuously. That is enough for frames sent every second or faster.");
        }

        if (stopped || phases.Count < 2)
        {
            _io.WriteLine("Stopped before the steps were done; nothing ranked. The frames are saved.");
            return "stopped";
        }

        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return "stopped";
        }

        var ranked = BroadcastRanker.Rank(heardFrames, phases).Take(8).ToList();
        if (ranked.Count == 0)
        {
            _io.WriteLine($"Nothing on {Bus(bus)} followed the steps.");
            _status[target.Id] = "nothing yet";
            return "nothing";
        }

        _io.WriteLine();
        _io.WriteLine($"What followed you on {Bus(bus)}, likeliest first:");
        _io.WriteLine("  #  FRAME     FIELD                 EACH STEP" + new string(' ', 14) + "SCORE");
        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1}  {c.IdText,-8}  {c.Field,-20}  {string.Join(' ', c.Values),-22}  {c.Score:0.00}{(c.CarriedForward ? "  (sent on change)" : "")}"));
        }

        var verdicts = new Dictionary<int, string>();
        while (true)
        {
            _io.WriteLine();
            var pick = Ask("Number to check it live (do it again and watch it change), Enter when finished");
            if (pick.Length == 0)
            {
                break;
            }

            if (int.TryParse(pick, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= ranked.Count)
            {
                verdicts[n - 1] = await LiveListenAsync(ranked[n - 1], bus, steps, ct).ConfigureAwait(false);
                if (verdicts[n - 1] == "confirmed")
                {
                    break;
                }
            }
        }

        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            var evidence = string.Join(' ', steps.Take(c.Values.Count).Select((s, k) => $"{s.Label}={c.Values[k]}"));
            _output.Finding(_output.Now(), target.Name, target.Signal, "listen", Bus(bus), "", c.IdText, "", c.Field,
                $"mask {c.Mask:X2}", c.CarriedForward ? "sent on change" : "", evidence, c.Score, verdicts.GetValueOrDefault(i, "unchecked"), "");
        }

        var confirmed = verdicts.Any(v => v.Value == "confirmed");
        _status[target.Id] = confirmed ? $"CONFIRMED {ranked[verdicts.First(v => v.Value == "confirmed").Key].IdText}" : $"{ranked.Count} candidate(s)";
        return confirmed ? "confirmed" : "ranked";
    }

    /// <summary>Hear one frame alone and print its field each time it changes, while the person does it again.</summary>
    private async Task<string> LiveListenAsync(BroadcastCandidate candidate, CanBus bus, IReadOnlyList<HuntStep> steps, CancellationToken ct)
    {
        var labels = new Dictionary<int, string>();
        for (var i = 0; i < Math.Min(steps.Count, candidate.Values.Count); i++)
        {
            labels.TryAdd(candidate.Values[i], steps[i].Label);
        }

        _io.WriteLine();
        _io.WriteLine($"LIVE: {candidate.IdText} {candidate.Field}. Do it again a few times, slowly. Press Enter to stop.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listening = Task.Run(async () =>
        {
            int? last = null;
            try
            {
                await foreach (var frame in _session.Monitor.ListenAsync(bus, candidate.Id, stop.Token, Rate(bus)).ConfigureAwait(false))
                {
                    var value = candidate.Read(frame.Data);
                    if (value != last)
                    {
                        last = value;
                        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"  {frame.At.TotalSeconds,6:0.0}s   {value}   {(value is { } v && labels.TryGetValue(v, out var l) ? l : "")}"));
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        // Under simulation, the guide does it again itself.
        var acting = _session.Simulated
            ? Task.Run(async () =>
            {
                for (var i = 1; !stop.IsCancellationRequested; i++)
                {
                    await Task.Delay(1200, stop.Token).ConfigureAwait(false);
                    _session.Simulate(steps[i % steps.Count].Simulate);
                }
            }, CancellationToken.None)
            : Task.CompletedTask;

        await _io.ReadLineAsync(ct).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await listening.ConfigureAwait(false);
        try
        {
            await acting.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _session.Simulate(steps[0].Simulate);
        await _session.RestoreAsync(ct).ConfigureAwait(false);
        return Verdict();
    }
}
