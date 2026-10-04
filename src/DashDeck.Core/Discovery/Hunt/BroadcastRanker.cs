using System.Globalization;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.Core.Discovery.Hunt;

/// <summary>
/// A stretch of a listen during which the truck was held in one state: the door open, the seat
/// on LOW (ADR-0044).
/// </summary>
/// <param name="Label">What the person was asked for: <c>OPEN</c>, <c>LOW</c>.</param>
/// <param name="State">The state as a number. Phases with the same number should read the same.</param>
/// <param name="Start">When the person said it was done, from the start of the listen.</param>
/// <param name="End">When the hold ended.</param>
public sealed record HuntPhase(string Label, double State, TimeSpan Start, TimeSpan End);

/// <summary>Which part of a frame's data a candidate is: a byte, a nibble or a bit.</summary>
public enum FieldKind
{
    Byte,
    LowNibble,
    HighNibble,
    Bit,
}

/// <summary>A part of one frame that followed what the person did.</summary>
/// <param name="Id">The frame's identifier.</param>
/// <param name="Byte">Which data byte, from 0.</param>
/// <param name="Kind">Byte, nibble or bit.</param>
/// <param name="Bit">For a bit, which one, 0 the least significant.</param>
/// <param name="Values">The value it read in each phase, in order.</param>
/// <param name="Stability">How steady it held within the phases, 0–1 (1 never wavered).</param>
/// <param name="CarriedForward">True when some phase saw no frame and used the one before — a frame sent only on change.</param>
/// <param name="Score">Higher is likelier.</param>
/// <param name="Separated">How many pairs of different states it read differently.</param>
/// <param name="Pairs">How many pairs of different states there were: 1 for on/off, 6 for four levels.</param>
public sealed record BroadcastCandidate(
    uint Id,
    int Byte,
    FieldKind Kind,
    int Bit,
    IReadOnlyList<int> Values,
    double Stability,
    bool CarriedForward,
    double Score,
    int Separated = 1,
    int Pairs = 1)
{
    /// <summary>True when it told every state apart — not only some of them.</summary>
    public bool SeparatesAll => Separated == Pairs;

    /// <summary>The value read in each phase, in order: <c>0 1 0 1</c>.</summary>
    public string ValuesText => string.Join(' ', Values);

    /// <summary>How steady it held, as a percentage.</summary>
    public string StabilityText => Stability.ToString("P0", CultureInfo.InvariantCulture);

    /// <summary>Pairs of different states it told apart, of all of them: <c>3/3</c>.</summary>
    public string SeparatesText => string.Create(CultureInfo.InvariantCulture, $"{Separated}/{Pairs}");

    /// <summary>The identifier as the adapter prints it.</summary>
    public string IdText => Id > 0x7FF ? Id.ToString("X8", CultureInfo.InvariantCulture) : Id.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>The field in words: <c>byte 2</c>, <c>byte 2 low nibble</c>, <c>byte 0 bit 1</c>.</summary>
    public string Field => Kind switch
    {
        FieldKind.Byte => $"byte {Byte}",
        FieldKind.LowNibble => $"byte {Byte} low nibble",
        FieldKind.HighNibble => $"byte {Byte} high nibble",
        _ => $"byte {Byte} bit {Bit}",
    };

    /// <summary>The mask over the byte: 0xFF, 0x0F, 0xF0 or one bit.</summary>
    public byte Mask => Kind switch
    {
        FieldKind.Byte => 0xFF,
        FieldKind.LowNibble => 0x0F,
        FieldKind.HighNibble => 0xF0,
        _ => (byte)(1 << Bit),
    };

    /// <summary>The field's value in one frame's data, or null when the frame is too short.</summary>
    public int? Read(byte[] data) => BroadcastRanker.Read(data, Byte, Kind, Bit);
}

/// <summary>
/// Finds the parts of the bus's traffic that changed with what a person did, and only with it
/// (ADR-0044).
/// </summary>
/// <remarks>
/// A field is a candidate when, in every phase, it held one value (a counter or a checksum never
/// does), the same state always read the same value, and at least two different states read
/// differently. One that tells every state apart ranks above one that tells only some apart — a
/// seat may report only on and off, or two of three levels the same — and the screen says which.
/// The first half-second of each phase is skipped, for the module to catch up. A frame sent only
/// when something changes may say nothing during a phase; its last value before the phase ended
/// stands in, and the candidate says so.
/// </remarks>
public static class BroadcastRanker
{
    /// <summary>How steady a field must hold within each phase.</summary>
    public const double MinimumStability = 0.9;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.5);

    public static IReadOnlyList<BroadcastCandidate> Rank(IReadOnlyList<CanFrame> frames, IReadOnlyList<HuntPhase> phases)
    {
        if (phases.Count < 2 || phases.Select(p => p.State).Distinct().Count() < 2)
        {
            return [];
        }

        var candidates = new List<BroadcastCandidate>();

        foreach (var group in frames.GroupBy(f => f.Id))
        {
            var ordered = group.OrderBy(f => f.At).ToList();
            var length = ordered.Max(f => f.Data.Length);

            for (var b = 0; b < length; b++)
            {
                var found = new List<BroadcastCandidate>();

                foreach (var (kind, bit) in Fields())
                {
                    if (Evaluate(ordered, phases, b, kind, bit) is { } candidate)
                    {
                        found.Add(candidate);
                    }
                }

                candidates.AddRange(Smallest(found));
            }
        }

        return [.. candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Id).ThenBy(c => c.Byte)];
    }

    private static IEnumerable<(FieldKind Kind, int Bit)> Fields()
    {
        for (var bit = 0; bit < 8; bit++)
        {
            yield return (FieldKind.Bit, bit);
        }

        yield return (FieldKind.LowNibble, 0);
        yield return (FieldKind.HighNibble, 0);
        yield return (FieldKind.Byte, 0);
    }

    /// <summary>
    /// Of the fields in one byte that all explain it, keep the narrowest: a bit over the nibble
    /// holding it, a nibble over the byte.
    /// </summary>
    private static IEnumerable<BroadcastCandidate> Smallest(List<BroadcastCandidate> found)
    {
        if (found.Count == 0)
        {
            return found;
        }

        // What tells the most states apart comes first; a bit that only half explains a level
        // does not beat the nibble that holds it.
        var most = found.Max(c => c.Separated);
        found = [.. found.Where(c => c.Separated == most)];

        var bits = found.Where(c => c.Kind == FieldKind.Bit).ToList();
        if (bits.Count > 0)
        {
            return bits;
        }

        var nibbles = found.Where(c => c.Kind is FieldKind.LowNibble or FieldKind.HighNibble).ToList();
        return nibbles.Count > 0 ? nibbles : found;
    }

    internal static int? Read(byte[] data, int index, FieldKind kind, int bit)
    {
        if (index >= data.Length)
        {
            return null;
        }

        var value = data[index];
        return kind switch
        {
            FieldKind.Byte => value,
            FieldKind.LowNibble => value & 0x0F,
            FieldKind.HighNibble => value >> 4,
            _ => (value >> bit) & 1,
        };
    }

    private static BroadcastCandidate? Evaluate(List<CanFrame> frames, IReadOnlyList<HuntPhase> phases, int index, FieldKind kind, int bit)
    {
        var values = new int[phases.Count];
        var stabilities = new double[phases.Count];
        var carried = false;

        for (var p = 0; p < phases.Count; p++)
        {
            var phase = phases[p];
            var from = phase.Start + Settle < phase.End ? phase.Start + Settle : phase.Start;

            var samples = frames
                .Where(f => f.At >= from && f.At <= phase.End)
                .Select(f => Read(f.Data, index, kind, bit))
                .OfType<int>()
                .ToList();

            if (samples.Count == 0)
            {
                // Nothing during the phase: the last word before it ended still stands.
                var before = frames.LastOrDefault(f => f.At <= phase.End);
                if (before is null || Read(before.Data, index, kind, bit) is not { } held)
                {
                    return null;
                }

                carried = true;
                values[p] = held;
                stabilities[p] = 1;
                continue;
            }

            var mode = samples.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
            values[p] = mode.Key;
            stabilities[p] = (double)mode.Count() / samples.Count;
        }

        if (stabilities.Any(s => s < MinimumStability))
        {
            return null;
        }

        // The same state must read the same; different states must read differently.
        var byState = new Dictionary<double, int>();
        for (var p = 0; p < phases.Count; p++)
        {
            if (byState.TryGetValue(phases[p].State, out var seen))
            {
                if (seen != values[p])
                {
                    return null;
                }
            }
            else
            {
                byState[phases[p].State] = values[p];
            }
        }

        var states = byState.ToList();
        var pairs = 0;
        var separated = 0;
        for (var a = 0; a < states.Count; a++)
        {
            for (var b = a + 1; b < states.Count; b++)
            {
                pairs++;
                if (states[a].Value != states[b].Value)
                {
                    separated++;
                }
            }
        }

        if (separated == 0)
        {
            return null;
        }

        var stability = stabilities.Average();
        var separation = (double)separated / pairs;

        // A field that rises with the state (a seat level, a fan speed) is likelier than one that
        // merely differs; a value carried forward is weaker evidence than one heard in the phase.
        var ordered = byState.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var monotonic = ordered.Zip(ordered.Skip(1)).All(pair => pair.Second > pair.First) ||
                        ordered.Zip(ordered.Skip(1)).All(pair => pair.Second < pair.First);

        var score = (stability * (0.5 + (0.5 * separation))) + (monotonic && byState.Count > 2 ? 0.2 : 0) - (carried ? 0.1 : 0);

        return new BroadcastCandidate(frames[0].Id, index, kind, bit, values, stability, carried, Math.Round(score, 3), separated, pairs);
    }
}
