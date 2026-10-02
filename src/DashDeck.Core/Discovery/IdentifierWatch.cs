using DashDeck.Abstractions;
using DashDeck.Vehicle;

namespace DashDeck.Core.Discovery;

/// <summary>
/// One identifier being watched: what it said on the watch's first pass, what it says now, and how
/// far it has moved since.
/// </summary>
/// <remarks>
/// The baseline is the watch's own first reading, not the sweep's. The sweep may be minutes or days
/// old, and comparing against it made every value that had drifted since look as if it moved while
/// being watched — the first version did exactly that, and every row read MOVED ×1.
/// </remarks>
public sealed class WatchedIdentifier
{
    internal WatchedIdentifier(ushort did, int length)
    {
        Did = did;
        Length = length;
    }

    public ushort Did { get; }

    /// <summary>How many bytes it answered in the sweep — what decides how it is read.</summary>
    public int Length { get; }

    /// <summary>The bytes of the watch's first reading, or null before it has been read.</summary>
    public byte[]? First { get; private set; }

    /// <summary>The bytes it answered last, or null before it has been read.</summary>
    public byte[]? Current { get; private set; }

    /// <summary>The whole answer as one unsigned big-endian number — its lowest so far.</summary>
    public long Low { get; private set; }

    /// <summary>Its highest so far.</summary>
    public long High { get; private set; }

    /// <summary>Its lowest so far read as signed — the same as <see cref="Low"/> unless <see cref="MaybeSigned"/>.</summary>
    public long SignedLow { get; private set; }

    /// <summary>Its highest so far read as signed.</summary>
    public long SignedHigh { get; private set; }

    /// <summary>How many reads came back different from the read before.</summary>
    public int Changes { get; private set; }

    /// <summary>How many reads came back at all.</summary>
    public int Reads { get; private set; }

    /// <summary>True when the last read got no answer — the value shown is the one before.</summary>
    public bool Missed { get; private set; }

    /// <summary>True once it has been read at least once in this watch.</summary>
    public bool HasValue => First is not null;

    public bool HasChanged => Changes > 0;

    /// <summary>
    /// True when a two-byte value has had its top bit set — it may be signed, and reading it as
    /// unsigned would turn −14 into 65522.
    /// </summary>
    public bool MaybeSigned => Length == 2 && HasValue && (High & 0x8000) != 0;

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

    /// <summary>A two-byte answer as a signed number: <c>FF F2</c> is −14.</summary>
    public static long SignedValueOf(byte[] data) =>
        data.Length == 2 ? (short)((data[0] << 8) | data[1]) : ValueOf(data);

    internal void Record(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            Missed = true;
            return;
        }

        Missed = false;
        Reads++;
        var value = ValueOf(data);
        var signed = SignedValueOf(data);

        if (First is null)
        {
            First = data;
            Low = High = value;
            SignedLow = SignedHigh = signed;
        }
        else if (Current is not null && !data.AsSpan().SequenceEqual(Current))
        {
            Changes++;
        }

        Current = data;
        Low = Math.Min(Low, value);
        High = Math.Max(High, value);
        SignedLow = Math.Min(SignedLow, signed);
        SignedHigh = Math.Max(SignedHigh, signed);
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
            .Select(g => new WatchedIdentifier(g.Key, g.First().Data.Length))
            .OrderBy(w => w.Did)];
    }

    /// <summary>Everything being watched, in identifier order.</summary>
    public IReadOnlyList<WatchedIdentifier> Items => _items;

    /// <summary>Complete passes so far.</summary>
    public int Passes { get; private set; }

    /// <summary>How many have changed at least once.</summary>
    public int ChangedCount => _items.Count(i => i.HasChanged);

    /// <summary>
    /// What changed first — most changes, then widest range — then what never changed, then what
    /// has not been read yet, each in identifier order.
    /// </summary>
    public IReadOnlyList<WatchedIdentifier> Ranked() =>
    [
        .. _items.Where(i => i.HasChanged).OrderByDescending(i => i.Changes).ThenByDescending(i => i.High - i.Low).ThenBy(i => i.Did),
        .. _items.Where(i => i.HasValue && !i.HasChanged),
        .. _items.Where(i => !i.HasValue),
    ];

    /// <summary>
    /// Roughly how long one pass takes at the adapter's measured rate (~19 a second, Q12) — so the
    /// screen can say how long to leave it before doing anything.
    /// </summary>
    public TimeSpan PassTime(double requestsPerSecond = 19) =>
        TimeSpan.FromSeconds(Math.Max(1, _items.Count / Math.Max(1, requestsPerSecond)));

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
