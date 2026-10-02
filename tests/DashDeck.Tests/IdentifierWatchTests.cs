using DashDeck.Abstractions;
using DashDeck.Core.Discovery;
using DashDeck.Vehicle;

namespace DashDeck.Tests;

/// <summary>WATCH: re-asking what a sweep found, to find what moves.</summary>
public class IdentifierWatchTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static IdentifierWatch Watch(params FoundIdentifier[] found) => new(found);

    [Fact]
    public void Only_short_answers_are_watched_not_refusals_or_text()
    {
        var watch = Watch(
            new FoundIdentifier(0x1001, [0x10], null),
            new FoundIdentifier(0x1002, [0x01, 0x02, 0x03, 0x04], null),
            new FoundIdentifier(0x1003, [], 0x33),
            new FoundIdentifier(0xF190, [.. "1FTEW1EP0KFA00000"u8.ToArray()], null));

        Assert.Equal([0x1001, 0x1002], watch.Items.Select(i => (int)i.Did));
    }

    /// <summary>
    /// The baseline is the watch's first reading, not the sweep's: a value that drifted between the
    /// sweep and the watch has not moved while being watched. The first version compared with the
    /// sweep, and in the truck every row read MOVED ×1 before a single pass had finished.
    /// </summary>
    [Fact]
    public void A_value_that_differs_from_the_sweep_is_not_moved_until_it_changes_while_watched()
    {
        var watch = Watch(new FoundIdentifier(0x1E1C, [0x05, 0x75], null));
        var item = Assert.Single(watch.Items);

        Assert.False(item.HasValue);

        watch.Record(0x1E1C, [0x05, 0x96]);
        Assert.True(item.HasValue);
        Assert.False(item.HasChanged);
        Assert.Equal([0x05, 0x96], item.First);

        watch.Record(0x1E1C, [0x05, 0x96]);
        Assert.False(item.HasChanged);

        watch.Record(0x1E1C, [0x05, 0x97]);
        Assert.Equal(1, item.Changes);
        Assert.Equal(0x0596, item.Low);
        Assert.Equal(0x0597, item.High);
    }

    [Fact]
    public void A_value_that_moves_rises_to_the_top_a_still_one_sinks_and_an_unread_one_is_last()
    {
        var watch = Watch(
            new FoundIdentifier(0x1001, [0x20], null),
            new FoundIdentifier(0x1002, [0x00, 0x50], null),
            new FoundIdentifier(0x1003, [0x7F], null),
            new FoundIdentifier(0x1004, [0x01], null));

        // Pass one: the baseline. Pass two and three: 1002 moves twice, 1003 once, 1001 never.
        // 1004 is never answered.
        foreach (var (a, b, c) in new (byte, byte[], byte)[] { (0x20, [0x00, 0x50], 0x7F), (0x20, [0x00, 0x58], 0x80), (0x20, [0x00, 0x51], 0x80) })
        {
            watch.Record(0x1001, [a]);
            watch.Record(0x1002, b);
            watch.Record(0x1003, [c]);
        }

        var ranked = watch.Ranked();
        Assert.Equal([0x1002, 0x1003, 0x1001, 0x1004], ranked.Select(i => (int)i.Did));

        var moving = ranked[0];
        Assert.Equal(2, moving.Changes);
        Assert.Equal(0x50, moving.Low);
        Assert.Equal(0x58, moving.High);
        Assert.Equal([0x00, 0x50], moving.First);
        Assert.Equal([0x00, 0x51], moving.Current);
        Assert.False(ranked[2].HasChanged);
        Assert.False(ranked[3].HasValue);
        Assert.Equal(2, watch.ChangedCount);
    }

    [Fact]
    public void A_missed_read_keeps_the_last_value_and_is_not_a_change()
    {
        var watch = Watch(new FoundIdentifier(0x1001, [0x20], null));
        watch.Record(0x1001, [0x20]);

        watch.Record(0x1001, null);

        var item = Assert.Single(watch.Items);
        Assert.True(item.Missed);
        Assert.False(item.HasChanged);
        Assert.Equal([0x20], item.Current);

        watch.Record(0x1001, [0x20]);
        Assert.False(item.Missed);
    }

    /// <summary>FF F2 is −14, not 65522 — the low–high of 48–65522 seen in the truck was this.</summary>
    [Fact]
    public void A_two_byte_value_with_its_top_bit_set_may_be_signed()
    {
        var watch = Watch(new FoundIdentifier(0x1E14, [0x00, 0x30], null));
        watch.Record(0x1E14, [0x00, 0x30]);
        Assert.False(watch.Items[0].MaybeSigned);

        watch.Record(0x1E14, [0xFF, 0xF2]);
        Assert.True(watch.Items[0].MaybeSigned);
        Assert.Equal(-14, WatchedIdentifier.SignedValueOf([0xFF, 0xF2]));
        Assert.Equal(48, WatchedIdentifier.SignedValueOf([0x00, 0x30]));
    }

    [Fact]
    public void A_pass_of_82_takes_about_four_seconds_at_the_measured_rate() =>
        Assert.InRange(new IdentifierWatch(Enumerable.Range(0x1000, 82).Select(d => new FoundIdentifier((ushort)d, [0x01], null))).PassTime().TotalSeconds, 4, 5);

    [Fact]
    public void Values_are_read_big_endian_as_one_number() =>
        Assert.Equal(0x0102, WatchedIdentifier.ValueOf([0x01, 0x02]));

    [Fact]
    public async Task A_pass_asks_each_watched_identifier_of_its_module_once()
    {
        var asked = new List<PidRequest>();
        var reading = (byte)0x60;
        Task<PidResponse> Answer(PidRequest r, CancellationToken ct)
        {
            asked.Add(r);
            return Task.FromResult(r.Pid == 0x1001
                ? PidResponse.Ok(r, [reading++], At)
                : PidResponse.Ok(r, [0x10], At));
        }

        var watch = Watch(new FoundIdentifier(0x1001, [0x5F], null), new FoundIdentifier(0x1002, [0x10], null));

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.True(await watch.PassAsync(Answer, CanBus.Hs, 0x7E0, null, CancellationToken.None));
        }

        Assert.Equal(3, watch.Passes);
        Assert.Equal(6, asked.Count);
        Assert.All(asked, r => Assert.Equal(new PidRequest(0x22, r.Pid, CanBus.Hs, 0x7E0), r));
        Assert.Equal(0x1001, watch.Ranked()[0].Did);
        Assert.Equal(2, watch.Ranked()[0].Changes);
        Assert.Equal([0x60], watch.Ranked()[0].First);
    }

    [Fact]
    public async Task Stopping_ends_a_pass_part_way_and_does_not_count_it()
    {
        using var cts = new CancellationTokenSource();
        Task<PidResponse> Answer(PidRequest r, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromResult(PidResponse.Ok(r, [0x01], At));
        }

        var watch = Watch(new FoundIdentifier(0x1001, [0x01], null), new FoundIdentifier(0x1002, [0x01], null));

        Assert.False(await watch.PassAsync(Answer, CanBus.Hs, 0x7E0, null, cts.Token));
        Assert.Equal(0, watch.Passes);
    }
}
