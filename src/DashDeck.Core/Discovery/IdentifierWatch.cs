using DashDeck.Abstractions;
using DashDeck.Vehicle;

namespace DashDeck.Core.Discovery;

/// <summary>One identifier being watched: what it said first, what it says now, and how far it has moved.</summary>
public sealed class WatchedIdentifier
{
    internal WatchedIdentifier(ushort did, byte[] first)
    {
        Did = did;
        First = first;
        Current = first;
        Low = High = ValueOf(first);
    }

    public ushort Did { get; }

    /// <summary>The bytes it answered when the watch started.</summary>
    public byte[] First { get; }

    /// <summary>The bytes it answered last.</summary>
    public byte[] Current { get; private set; }

    /// <summary>The whole answer as one unsigned big-endian number — its lowest so far.</summary>
    public long Low { get; private set; }

    /// <summary>Its highest so far.</summary>
    public long High { get; private set; }

    /// <summary>How many reads came back different from the read before.</summary>
    public int Changes { get; private set; }

    /// <summary>How many reads came back at all.</summary>
    public int Reads { get; private set; }

    /// <summary>True when the last read got no answer — the value shown is the one before.</summary>
    public bool Missed { get; private set; }

    public bool HasChanged => Changes > 0;

    /// <summary>The whole answer as one unsigned big-endian number.</summary>
    public static long ValueOf(byte[] data)
    {
        long value = 0;
        foreach (var b in data.Take(4))
        {
            value = (value << 8) | b;
        }

        return value;
    }

    internal void Record(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            Missed = true;
            return;
        }

        Missed = false;
        Reads++;

        if (!data.AsSpan().SequenceEqual(Current))
        {
            Changes++;
        }

        Current = data;
        var value = ValueOf(data);
        Low = Math.Min(Low, value);
        High = Math.Max(High, value);
    }
}

/// <summary>
/// Watches the identifiers a sweep found, over and over, to find the ones that move (ADR-0035's
/// next step).
/// </summary>
/// <remarks>
/// A sweep of a Ford range can answer a hundred identifiers or more, and testing each by hand —
/// tap, TEST, change something, TEST again — is a needle in a haystack. This re-asks only the ones
/// that answered, round and round, and keeps what each said first, now, and its range. Do one
/// thing to the truck while it runs — blip the throttle, or let it warm — and what changes with it
/// rises to the top of <see cref="Ranked"/>; what never changes sinks.
/// <para>
/// Reads only, the same <c>22</c> requests the sweep sent. Only values short enough to be one
/// number (1–4 bytes) are watched: longer ones are text — part numbers, the VIN.
/// </para>
/// </remarks>
public sealed class IdentifierWatch
{
    private readonly List<WatchedIdentifier> _items;

    /// <param name="found">What a sweep found. Refusals and long answers are left out.</param>
    public IdentifierWatch(IEnumerable<FoundIdentifier> found)
    {
        ArgumentNullException.ThrowIfNull(found);

        _items = [.. found
            .Where(f => f.RefusalCode is null && f.Data.Length is > 0 and <= 4)
            .GroupBy(f => f.Did)
            .Select(g => new WatchedIdentifier(g.Key, g.First().Data))
            .OrderBy(w => w.Did)];
    }

    /// <summary>Everything being watched, in identifier order.</summary>
    public IReadOnlyList<WatchedIdentifier> Items => _items;

    /// <summary>Complete passes so far.</summary>
    public int Passes { get; private set; }

    /// <summary>How many have changed at least once.</summary>
    public int ChangedCount => _items.Count(i => i.HasChanged);

    /// <summary>
    /// What changed first — most changes, then widest range — and what never changed after, in
    /// identifier order.
    /// </summary>
    public IReadOnlyList<WatchedIdentifier> Ranked() =>
    [
        .. _items.Where(i => i.HasChanged).OrderByDescending(i => i.Changes).ThenByDescending(i => i.High - i.Low).ThenBy(i => i.Did),
        .. _items.Where(i => !i.HasChanged),
    ];

    /// <summary>Record one read of one identifier: its bytes, or null for no answer.</summary>
    public void Record(ushort did, byte[]? data) =>
        _items.Find(i => i.Did == did)?.Record(data);

    /// <summary>
    /// Ask every watched identifier once. Returns false when it was stopped part-way.
    /// </summary>
    public async Task<bool> PassAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> request,
        CanBus bus,
        ushort module,
        IProgress<SweepProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var n = 0; n < _items.Count; n++)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            var item = _items[n];
            progress?.Report(new SweepProgress(n, _items.Count, ChangedCount, $"22 {item.Did:X4}"));

            try
            {
                var response = await request(
                    new PidRequest(ModuleScanner.ReadDataByIdentifier, item.Did, bus, module), ct)
                    .ConfigureAwait(false);

                item.Record(response.IsSuccess ? response.Data : null);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        Passes++;
        progress?.Report(new SweepProgress(_items.Count, _items.Count, ChangedCount, ""));
        return true;
    }
}
