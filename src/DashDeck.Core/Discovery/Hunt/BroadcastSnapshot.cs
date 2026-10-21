namespace DashDeck.Core.Discovery.Hunt;

/// <summary>One broadcast frame as it was last heard during a short listen.</summary>
/// <param name="Id">The frame's CAN identifier.</param>
/// <param name="Data">Its bytes, the last time it was heard.</param>
/// <param name="Changing">For each byte, whether it took more than one value while listening.</param>
/// <param name="Heard">How many times it was heard.</param>
public sealed record HeardFrame(uint Id, byte[] Data, bool[] Changing, int Heard);

/// <summary>
/// What a bus was saying during one reading of a match: every frame heard, its last bytes, and which
/// bytes would not keep still (ADR-0070).
/// </summary>
/// <remarks>
/// <b>A number someone can read off a display is steady while they read it.</b> A set speed, a trip
/// figure, a setting: none moves within the two or three seconds a listen takes. Counters, checksums,
/// rpm and road speed all do. So a byte that took two values in the window is not a candidate, and
/// neither is any reading that covers it. That one rule removes most of what makes a broadcast match
/// noisy, without the separate noise-learning step a listen by states needs.
/// <para>
/// Frames are matched with the same <see cref="MatchRanker"/> as identifiers, keyed by frame id rather
/// than by identifier. An 11-bit id fits the same key, and the caller keeps the two apart by which
/// tally it adds them to.
/// </para>
/// </remarks>
public sealed class BroadcastSnapshot
{
    private readonly Dictionary<uint, HeardFrame> _frames;

    private BroadcastSnapshot(Dictionary<uint, HeardFrame> frames) => _frames = frames;

    /// <summary>Every frame heard, by id.</summary>
    public IReadOnlyDictionary<uint, HeardFrame> Frames => _frames;

    /// <summary>Gather a listen's frames, given as (id, data) in the order they were heard.</summary>
    public static BroadcastSnapshot From(IEnumerable<(uint Id, byte[] Data)> heard)
    {
        ArgumentNullException.ThrowIfNull(heard);

        var frames = new Dictionary<uint, HeardFrame>();

        foreach (var (id, data) in heard)
        {
            if (!frames.TryGetValue(id, out var before))
            {
                frames[id] = new HeardFrame(id, [.. data], new bool[data.Length], 1);
                continue;
            }

            // A frame that changed length is treated as changing everywhere: its bytes no longer line up.
            var changing = new bool[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                changing[i] = data.Length != before.Data.Length
                    || before.Changing[i]
                    || before.Data[i] != data[i];
            }

            frames[id] = new HeardFrame(id, [.. data], changing, before.Heard + 1);
        }

        return new BroadcastSnapshot(frames);
    }

    /// <summary>
    /// The frames' bytes in the shape <see cref="MatchRanker.Find"/> takes. Ids above 16 bits (29-bit
    /// extended frames) are left out; the trucks this is for broadcast 11-bit ones.
    /// </summary>
    public IReadOnlyDictionary<ushort, byte[]> Answers =>
        _frames.Values.Where(f => f.Id <= ushort.MaxValue).ToDictionary(f => (ushort)f.Id, f => f.Data);

    /// <summary>Whether every byte a hit reads kept still for the whole listen.</summary>
    public bool IsSteady(MatchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        if (!_frames.TryGetValue(hit.Did, out var frame))
        {
            return false;
        }

        for (var i = hit.Reading.Offset; i < hit.Reading.Offset + hit.Reading.Length; i++)
        {
            if (i >= frame.Changing.Length || frame.Changing[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The number a person typed, matched against every steady field of every frame heard.
    /// </summary>
    public IReadOnlyList<MatchHit> Find(double shown, int decimals) =>
        [.. MatchRanker.Find(Answers, shown, decimals).Where(IsSteady)];
}
