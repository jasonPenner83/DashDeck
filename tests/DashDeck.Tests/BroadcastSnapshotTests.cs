using DashDeck.Core.Discovery.Hunt;

namespace DashDeck.Tests;

/// <summary>
/// What a bus said during one reading of a match, and the rule that keeps counters out (ADR-0070).
/// </summary>
public sealed class BroadcastSnapshotTests
{
    private static readonly byte[] SixtyKph = [0x00, 0x00, 0x78, 0x00];   // 120 halves of a km/h in bytes 1-2

    private static byte[] WithCounter(byte counter) => [counter, 0x00, 0x78, 0x00];

    [Fact]
    public void The_last_bytes_of_each_frame_are_kept()
    {
        var snapshot = BroadcastSnapshot.From([(0x3F2, WithCounter(1)), (0x420, [0x82]), (0x3F2, WithCounter(2))]);

        Assert.Equal(2, snapshot.Frames.Count);
        Assert.Equal(WithCounter(2), snapshot.Frames[0x3F2].Data);
        Assert.Equal(2, snapshot.Frames[0x3F2].Heard);
    }

    [Fact]
    public void A_byte_that_moved_while_listening_is_marked_and_the_rest_are_not()
    {
        var snapshot = BroadcastSnapshot.From([(0x3F2, WithCounter(1)), (0x3F2, WithCounter(2)), (0x3F2, WithCounter(1))]);

        Assert.Equal([true, false, false, false], snapshot.Frames[0x3F2].Changing);
    }

    [Fact]
    public void A_steady_value_beside_a_counter_is_found_and_the_counter_is_not()
    {
        var snapshot = BroadcastSnapshot.From([(0x3F2, WithCounter(60)), (0x3F2, WithCounter(61)), (0x3F2, WithCounter(62))]);

        var hits = snapshot.Find(60, decimals: 0);

        Assert.Contains(hits, h => h.Reading == new Reading(1, 2, false) && h.Transform.Scale == 0.5);
        Assert.DoesNotContain(hits, h => h.Reading.Offset == 0);
    }

    [Fact]
    public void A_frame_heard_once_counts_as_steady()
    {
        // Nothing seen to change — the same as an identifier read once.
        var snapshot = BroadcastSnapshot.From([(0x3F2, SixtyKph)]);

        Assert.NotEmpty(snapshot.Find(60, decimals: 0));
    }

    [Fact]
    public void A_frame_that_changed_length_is_changing_everywhere()
    {
        var snapshot = BroadcastSnapshot.From([(0x3F2, SixtyKph), (0x3F2, [0x00, 0x00, 0x78])]);

        Assert.All(snapshot.Frames[0x3F2].Changing, Assert.True);
        Assert.Empty(snapshot.Find(60, decimals: 0));
    }

    [Fact]
    public void A_hit_on_a_frame_that_was_not_heard_is_not_steady()
    {
        var snapshot = BroadcastSnapshot.From([(0x3F2, SixtyKph)]);
        var stranger = new MatchHit(0x123, new Reading(0, 1, false), new MatchTransform(1, 0, ""), 60, 60);

        Assert.False(snapshot.IsSteady(stranger));
    }

    [Fact]
    public void Extended_frames_are_left_out_of_the_answers()
    {
        var snapshot = BroadcastSnapshot.From([(0x18FEF100u, SixtyKph), (0x3F2, SixtyKph)]);

        Assert.Single(snapshot.Answers);
        Assert.True(snapshot.Answers.ContainsKey(0x3F2));
    }
}
