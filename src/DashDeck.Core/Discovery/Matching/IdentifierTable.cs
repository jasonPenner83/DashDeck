namespace DashDeck.Core.Discovery.Matching;

/// <summary>What has been heard from one identifier.</summary>
public sealed class IdentifierStats
{
    /// <summary>History kept per identifier; at FORScan's 35 a second that is many minutes.</summary>
    public const int HistoryLimit = 50_000;

    private readonly List<Observation> _history = [];

    public IdentifierStats(IdentifierKey key) => Key = key;

    public IdentifierKey Key { get; }

    public int Count { get; private set; }

    /// <summary>How many times the answer differed from the one before.</summary>
    public int Changes { get; private set; }

    public DateTimeOffset FirstSeen { get; private set; }

    public DateTimeOffset LastSeen { get; private set; }

    public byte[] LastPayload { get; private set; } = [];

    /// <summary>Every answer, oldest first (the oldest dropped past <see cref="HistoryLimit"/>).</summary>
    public IReadOnlyList<Observation> History => _history;

    internal void Add(Observation observation)
    {
        if (Count == 0)
        {
            FirstSeen = observation.At;
        }
        else if (!observation.Payload.AsSpan().SequenceEqual(LastPayload))
        {
            Changes++;
        }

        Count++;
        LastSeen = observation.At;
        LastPayload = observation.Payload;

        if (_history.Count >= HistoryLimit)
        {
            _history.RemoveRange(0, HistoryLimit / 10);
        }

        _history.Add(observation);
    }

    /// <summary>The answer in force at <paramref name="at"/>: the last one heard at or before it.</summary>
    public byte[]? PayloadAt(DateTimeOffset at)
    {
        var lo = 0;
        var hi = _history.Count - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_history[mid].At <= at)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 ? _history[found].Payload : null;
    }
}

/// <summary>Every identifier heard, in the order first heard.</summary>
public sealed class IdentifierTable
{
    private readonly Dictionary<IdentifierKey, IdentifierStats> _byKey = [];
    private readonly List<IdentifierStats> _ordered = [];
    private readonly object _gate = new();

    /// <summary>Raised when an identifier is heard for the first time.</summary>
    public event Action<IdentifierStats>? Added;

    public IReadOnlyList<IdentifierStats> Snapshot()
    {
        lock (_gate)
        {
            return [.. _ordered];
        }
    }

    public IdentifierStats? Find(IdentifierKey key)
    {
        lock (_gate)
        {
            return _byKey.GetValueOrDefault(key);
        }
    }

    public void Add(Observation observation)
    {
        IdentifierStats? added = null;

        lock (_gate)
        {
            if (!_byKey.TryGetValue(observation.Key, out var stats))
            {
                stats = new IdentifierStats(observation.Key);
                _byKey[observation.Key] = stats;
                _ordered.Add(stats);
                added = stats;
            }

            stats.Add(observation);
        }

        if (added is not null)
        {
            Added?.Invoke(added);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byKey.Clear();
            _ordered.Clear();
        }
    }
}
