using System.Globalization;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.Core.Discovery.Hunt;

/// <summary>One identifier heard while listening, as the LISTEN tab shows it.</summary>
/// <param name="Id">The CAN identifier.</param>
/// <param name="Frames">Frames heard with it.</param>
/// <param name="RateHz">How often it is sent, over the listen so far.</param>
/// <param name="Data">Its latest bytes.</param>
/// <param name="ChangedSinceMark">Which bytes differ from what it carried at MARK (bit n = byte n), noise left out.</param>
/// <param name="ChangesSinceMark">Frames since MARK whose bytes differed from the frame before, noise left out.</param>
/// <param name="Noise">Bytes that moved while learning noise (bit n = byte n): left out of both counts.</param>
/// <param name="NewSinceMark">True when it was first heard after MARK.</param>
public sealed record WatchedId(
    uint Id,
    int Frames,
    double RateHz,
    byte[] Data,
    int ChangedSinceMark,
    int ChangesSinceMark,
    int Noise,
    bool NewSinceMark)
{
    /// <summary>How often it is sent, to one decimal.</summary>
    public string RateText => RateHz.ToString("0.#", CultureInfo.InvariantCulture);

    public string IdText => Id > 0x7FF ? Id.ToString("X8", CultureInfo.InvariantCulture) : Id.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>The bytes as hex, a changed one in brackets and a noisy one marked with a tilde: <c>01 [A0] ~3F</c>.</summary>
    public string DataText => string.Join(' ', Data.Select((b, i) =>
    {
        var hex = b.ToString("X2", CultureInfo.InvariantCulture);
        return (ChangedSinceMark & (1 << i)) != 0 ? $"[{hex}]" : (Noise & (1 << i)) != 0 ? $"~{hex}" : hex;
    }));

    /// <summary>The changed bytes in words: <c>byte 1, byte 4</c>.</summary>
    public string ChangedText => ChangedSinceMark == 0
        ? ""
        : string.Join(", ", Enumerable.Range(0, 8).Where(i => (ChangedSinceMark & (1 << i)) != 0).Select(i => $"byte {i}"));
}

/// <summary>
/// Free listening, for doing something in the truck and seeing what changed (ADR-0054).
/// </summary>
/// <remarks>
/// Fed every frame the silent monitor hears. Three ways to narrow down:
/// <list type="bullet">
/// <item><b>Noise:</b> while learning, every byte that moves on its own — counters, checksums,
/// engine values — is marked and left out of everything after.</item>
/// <item><b>Mark:</b> a baseline. From then on, what differs from it, and how often each
/// identifier changed, is counted; the person does one thing and looks.</item>
/// <item><b>States:</b> the person holds the truck one way (A), then the other (B), a few times;
/// <see cref="BroadcastRanker"/> ranks the fields that are steady within each and differ between —
/// the ID hunter's listen, without a checklist.</item>
/// </list>
/// Thread-safe: frames arrive on the listen's worker, the screen reads on its own.
/// </remarks>
public sealed class BusWatch
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Entry> _ids = [];
    private readonly List<CanFrame> _frames = [];
    private readonly List<HuntPhase> _phases = [];
    private (string Label, double State, TimeSpan Start)? _open;
    private bool _learningNoise;
    private TimeSpan _now;
    private TimeSpan? _markAt;

    /// <summary>The most frames kept for ranking; past it the oldest are dropped.</summary>
    public int MaxFrames { get; init; } = 2_000_000;

    private sealed class Entry
    {
        public int Frames;
        public TimeSpan First;
        public byte[] Last = [];
        public byte[]? AtMark;
        public int Changes;
        public int Noise;
        public bool New;
    }

    /// <summary>Frames heard in all.</summary>
    public int Frames
    {
        get
        {
            lock (_gate)
            {
                return _ids.Values.Sum(e => e.Frames);
            }
        }
    }

    /// <summary>True while every moving byte is being marked as noise.</summary>
    public bool LearningNoise
    {
        get
        {
            lock (_gate)
            {
                return _learningNoise;
            }
        }
    }

    /// <summary>True once MARK has been pressed.</summary>
    public bool Marked
    {
        get
        {
            lock (_gate)
            {
                return _markAt is not null;
            }
        }
    }

    /// <summary>The states held so far, closed ones and the one open now.</summary>
    public IReadOnlyList<HuntPhase> Phases
    {
        get
        {
            lock (_gate)
            {
                return _open is { } open
                    ? [.. _phases, new HuntPhase(open.Label, open.State, open.Start, _now)]
                    : [.. _phases];
            }
        }
    }

    public void Add(CanFrame frame)
    {
        lock (_gate)
        {
            _now = frame.At;

            if (!_ids.TryGetValue(frame.Id, out var entry))
            {
                entry = new Entry { First = frame.At, New = _markAt is not null };
                _ids[frame.Id] = entry;
            }
            else if (entry.Last.Length > 0)
            {
                var moved = Moved(entry.Last, frame.Data);
                if (_learningNoise)
                {
                    entry.Noise |= moved;
                }
                else if (_markAt is not null && (moved & ~entry.Noise) != 0)
                {
                    entry.Changes++;
                }
            }

            entry.Frames++;
            entry.Last = frame.Data;
            entry.AtMark ??= _markAt is not null ? frame.Data : null;

            _frames.Add(frame);
            if (_frames.Count > MaxFrames)
            {
                _frames.RemoveRange(0, _frames.Count - MaxFrames);
            }
        }
    }

    /// <summary>Start or stop learning noise. Everything that moves meanwhile is left out after.</summary>
    public void LearnNoise(bool on)
    {
        lock (_gate)
        {
            _learningNoise = on;
        }
    }

    /// <summary>Forget what was learned as noise.</summary>
    public void ForgetNoise()
    {
        lock (_gate)
        {
            foreach (var entry in _ids.Values)
            {
                entry.Noise = 0;
            }
        }
    }

    /// <summary>Take a baseline now: what each identifier carries, and counts from zero.</summary>
    public void Mark()
    {
        lock (_gate)
        {
            _markAt = _now;
            foreach (var entry in _ids.Values)
            {
                entry.AtMark = entry.Last;
                entry.Changes = 0;
                entry.New = false;
            }
        }
    }

    /// <summary>
    /// Start holding a state — <c>A</c> (0) or <c>B</c> (1) — from now; the one before ends here.
    /// </summary>
    public void HoldState(string label, double state)
    {
        lock (_gate)
        {
            CloseState();
            _open = (label, state, _now);
        }
    }

    /// <summary>End the state being held, if any.</summary>
    public void EndState()
    {
        lock (_gate)
        {
            CloseState();
        }
    }

    /// <summary>Forget the states held, to start narrowing again.</summary>
    public void ClearStates()
    {
        lock (_gate)
        {
            _phases.Clear();
            _open = null;
        }
    }

    private void CloseState()
    {
        if (_open is { } open)
        {
            _phases.Add(new HuntPhase(open.Label, open.State, open.Start, _now));
            _open = null;
        }
    }

    /// <summary>Every identifier heard, in order.</summary>
    public IReadOnlyList<WatchedId> Snapshot()
    {
        lock (_gate)
        {
            return [.. _ids.OrderBy(p => p.Key).Select(p =>
            {
                var e = p.Value;
                var seconds = (_now - e.First).TotalSeconds;
                var changed = e.AtMark is { } mark ? Moved(mark, e.Last) & ~e.Noise : 0;
                return new WatchedId(p.Key, e.Frames, seconds > 0.5 ? (e.Frames - 1) / seconds : 0, e.Last, changed, e.Changes, e.Noise, e.New);
            })];
        }
    }

    /// <summary>
    /// The fields that told the states apart, best first — none until at least one A and one B
    /// have been held. Noise is left out.
    /// </summary>
    public IReadOnlyList<BroadcastCandidate> Rank()
    {
        List<CanFrame> frames;
        IReadOnlyList<HuntPhase> phases;
        Dictionary<uint, int> noise;

        lock (_gate)
        {
            frames = [.. _frames];
            phases = _open is { } open
                ? [.. _phases, new HuntPhase(open.Label, open.State, open.Start, _now)]
                : [.. _phases];
            noise = _ids.ToDictionary(p => p.Key, p => p.Value.Noise);
        }

        return [.. BroadcastRanker.Rank(frames, phases)
            .Where(c => (noise.GetValueOrDefault(c.Id) & (1 << c.Byte)) == 0)];
    }

    /// <summary>Every frame kept, for saving.</summary>
    public IReadOnlyList<CanFrame> AllFrames()
    {
        lock (_gate)
        {
            return [.. _frames];
        }
    }

    /// <summary>Bit n set when byte n differs, or exists in only one of the two.</summary>
    private static int Moved(byte[] before, byte[] after)
    {
        var mask = 0;
        for (var i = 0; i < Math.Max(before.Length, after.Length) && i < 8; i++)
        {
            if (i >= before.Length || i >= after.Length || before[i] != after[i])
            {
                mask |= 1 << i;
            }
        }

        return mask;
    }
}
