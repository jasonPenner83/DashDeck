using DashDeck.Abstractions;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.Tests;

/// <summary>Free listening in the ID matcher (ADR-0054): noise, MARK, and states A and B.</summary>
public sealed class BusWatchTests
{
    private static CanFrame F(double seconds, uint id, params byte[] data) =>
        new(TimeSpan.FromSeconds(seconds), CanBus.Ms, id, data);

    [Fact]
    public void What_moves_on_its_own_is_noise_and_after_mark_only_the_real_change_shows()
    {
        var watch = new BusWatch();
        byte counter = 0;

        // A counter in byte 0 of 4A0 ticks all the time; 3B3 sits still.
        void Second(double t, byte door)
        {
            watch.Add(F(t, 0x4A0, counter++, 0x00));
            watch.Add(F(t, 0x3B3, door, 0x40));
        }

        watch.LearnNoise(true);
        for (var t = 0.0; t < 2; t += 0.1)
        {
            Second(t, 0x00);
        }

        watch.LearnNoise(false);
        watch.Mark();

        for (var t = 2.0; t < 3; t += 0.1)
        {
            Second(t, 0x00);
        }

        // The door opens: byte 0 of 3B3 goes to 01.
        for (var t = 3.0; t < 4; t += 0.1)
        {
            Second(t, 0x01);
        }

        watch.Add(F(4, 0x5F0, 0xAA)); // first heard after MARK

        var rows = watch.Snapshot().ToDictionary(r => r.Id);
        Assert.Equal(0b1, rows[0x3B3].ChangedSinceMark);
        Assert.Equal(1, rows[0x3B3].ChangesSinceMark);
        Assert.Equal("[01] 40", rows[0x3B3].DataText);
        Assert.Equal("byte 0", rows[0x3B3].ChangedText);

        Assert.Equal(0, rows[0x4A0].ChangedSinceMark);
        Assert.Equal(0, rows[0x4A0].ChangesSinceMark);
        Assert.Equal(0b1, rows[0x4A0].Noise);
        Assert.StartsWith("~", rows[0x4A0].DataText, StringComparison.Ordinal);

        Assert.True(rows[0x5F0].NewSinceMark);
        Assert.False(rows[0x3B3].NewSinceMark);
        Assert.InRange(rows[0x3B3].RateHz, 9, 11);
    }

    [Fact]
    public void Holding_states_a_and_b_ranks_the_bit_that_tells_them_apart()
    {
        var watch = new BusWatch();
        var t = 0.0;
        byte counter = 0;

        void Hold(string label, double state, byte tailgate)
        {
            watch.HoldState(label, state);
            for (var i = 0; i < 30; i++, t += 0.1)
            {
                watch.Add(F(t, 0x3B3, (byte)(0x40 | tailgate), 0x00));
                watch.Add(F(t, 0x4A0, counter++, 0x00));
            }
        }

        Hold("A", 0, 0x00);
        Hold("B", 1, 0x08);
        Hold("A", 0, 0x00);
        Hold("B", 1, 0x08);

        var ranked = watch.Rank();

        var best = ranked[0];
        Assert.Equal(0x3B3u, best.Id);
        Assert.Equal(0, best.Byte);
        Assert.True(best.SeparatesAll);
        Assert.Contains("bit 3", best.Field, StringComparison.Ordinal);
        Assert.Equal(4, watch.Phases.Count);
    }

    [Fact]
    public void Noise_is_left_out_of_the_ranking_and_states_can_be_cleared()
    {
        var watch = new BusWatch();
        var t = 0.0;

        // Byte 1 of 2C0 happens to follow the states, but it was learned as noise.
        watch.LearnNoise(true);
        for (var i = 0; i < 10; i++, t += 0.1)
        {
            watch.Add(F(t, 0x2C0, 0x00, (byte)i));
        }

        watch.LearnNoise(false);
        foreach (var (label, state, value) in new[] { ("A", 0.0, (byte)0x00), ("B", 1.0, (byte)0x10), ("A", 0.0, (byte)0x00), ("B", 1.0, (byte)0x10) })
        {
            watch.HoldState(label, state);
            for (var i = 0; i < 20; i++, t += 0.1)
            {
                watch.Add(F(t, 0x2C0, 0x00, value));
            }
        }

        Assert.DoesNotContain(watch.Rank(), c => c.Id == 0x2C0 && c.Byte == 1);

        watch.ClearStates();
        Assert.Empty(watch.Phases);
        Assert.Empty(watch.Rank());
    }
}
