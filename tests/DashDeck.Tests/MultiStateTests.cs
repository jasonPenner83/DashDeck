using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery.Matching;
using DashDeck.Simulator;

namespace DashDeck.Tests;

/// <summary>Signals with several named states — 4WD, the gear selector, wipers (ADR-0056).</summary>
public class MultiStateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Shipped_multi_state_placeholders_name_their_states()
    {
        var catalog = TestCatalog.Load();

        var fourWd = catalog["drivetrain.4wdMode"];
        Assert.True(fourWd.Placeholder);
        Assert.Equal(["2H", "4A", "4H", "4L"], fourWd.States!.Select(s => s.Name));
        Assert.Equal("4H", fourWd.StateName(2));
        Assert.Null(fourWd.StateName(9));

        foreach (var id in new[] { "transmission.gearSelector", "vehicle.driveMode", "body.wipers", "body.headlights" })
        {
            Assert.True(catalog[id].HasStates, id);
            Assert.True(catalog[id].Placeholder, id);
        }
    }

    [Fact]
    public void States_are_checked_and_survive_a_round_trip()
    {
        var good = TestCatalog.Load()["drivetrain.4wdMode"] with { Placeholder = false, Pid = 0x1234, Mode = 0x22 };
        Assert.Empty(SignalCatalog.Check(good));

        var sameValue = good with { States = [new(0, "2H"), new(0, "4A")] };
        var sameName = good with { States = [new(0, "2H"), new(1, "2h")] };
        var noName = good with { States = [new(0, " ")] };
        Assert.Contains(SignalCatalog.Check(sameValue), p => p.Contains("share a value", StringComparison.Ordinal));
        Assert.Contains(SignalCatalog.Check(sameName), p => p.Contains("share a name", StringComparison.Ordinal));
        Assert.Contains(SignalCatalog.Check(noName), p => p.Contains("value and a name", StringComparison.Ordinal));

        var back = SignalCatalog.FromJson(SignalCatalog.ToJson([good]))["drivetrain.4wdMode"];
        Assert.Equal(good.States, back.States);

        var number = TestCatalog.Load()["vehicle.speed"];
        Assert.DoesNotContain("states", SignalCatalog.ToJson([number]), StringComparison.Ordinal);
    }

    // ── Matching typed states ────────────────────────────────────────────────

    /// <summary>
    /// A made-up answer: a rolling counter in byte 0, the 4WD mode in bits 4–5 of byte 1 among bits
    /// that never move, and a constant byte 2.
    /// </summary>
    private static byte[] Answer(int counter, int mode) => [(byte)counter, (byte)(0x83 | (mode << 4)), 0x5A];

    private static readonly string[] Modes = ["2H", "4A", "4H", "4L"];

    [Fact]
    public void Going_round_twice_leaves_the_field_that_holds_the_state()
    {
        // A counter that moves the way a real one does — not in steps that line up with the passes.
        int[] counter = [0x17, 0x4C, 0x91, 0xE2, 0x3A, 0x6F, 0xB8, 0x05];
        var samples = new List<(byte[], string)>();
        for (var i = 0; i < 8; i++)
        {
            samples.Add((Answer(counter[i], i % 4), Modes[i % 4]));
        }

        var candidates = ScalingFitter.FromNamedStates(samples);

        var best = Assert.Single(candidates);
        Assert.Equal(1, best.Window.Offset);
        Assert.Equal(0x30, best.Mask);
        Assert.Equal(["2H", "4A", "4H", "4L"], best.States!.Select(s => s.Name));
        Assert.Equal([0x00, 0x10, 0x20, 0x30], best.States!.Select(s => s.Value));
        Assert.Equal("4H", best.StateName(best.Apply(Answer(99, 2))!.Value));
        Assert.Contains("bits 4–5 of B", best.Formula, StringComparison.Ordinal);
    }

    [Fact]
    public void One_pass_also_fits_a_counter_which_the_second_pass_drops()
    {
        var once = Enumerable.Range(0, 4).Select(m => (Answer(m * 7, m), Modes[m])).ToList();
        var candidates = ScalingFitter.FromNamedStates(once);

        Assert.Contains(candidates, c => c.Window.Offset == 1 && c.Mask == 0x30);
        Assert.Contains(candidates, c => c.Window.Offset == 0);
        Assert.DoesNotContain(candidates, c => c.Window.Offset == 2);
    }

    [Fact]
    public void Names_are_one_state_whatever_the_case_and_a_state_must_read_the_same()
    {
        var samples = new List<(byte[], string)>
        {
            (Answer(1, 0), "2H"), (Answer(1, 1), "4A"), (Answer(1, 1), " 4a "), (Answer(1, 0), "2h"),
        };

        var best = ScalingFitter.FromNamedStates(samples)[0];
        Assert.Equal(2, best.States!.Count);
        Assert.Equal(["2H", "4A"], best.States!.Select(s => s.Name));

        // 4A read two different ways: nothing reads every state the same each time.
        var muddled = new List<(byte[], string)> { (Answer(1, 0), "2H"), (Answer(1, 1), "4A"), (Answer(1, 2), "4A") };
        Assert.DoesNotContain(ScalingFitter.FromNamedStates(muddled), c => c.Window.Offset == 1);
    }

    [Fact]
    public void A_paired_state_match_carries_the_trucks_values_names_and_mask()
    {
        int[] counter = [0x17, 0x4C, 0x91, 0xE2, 0x3A, 0x6F, 0xB8, 0x05];
        var samples = Enumerable.Range(0, 8).Select(i => (Answer(counter[i], i % 4), Modes[i % 4])).ToList();
        var candidate = ScalingFitter.FromNamedStates(samples).Single();
        var key = new IdentifierKey(CanBus.Hs, 0x7A0, 0x22, 0x1234);
        var match = new AcceptedMatch(key, "4x4 Mode", candidate, "", "8 states typed");
        var target = TestCatalog.Load()["drivetrain.4wdMode"];

        var paired = SignalPairing.Pair(match, target);

        Assert.Equal("drivetrain.4wdMode", paired.Id);
        Assert.False(paired.Placeholder);
        Assert.Equal(0x30, paired.Decode.Mask);
        Assert.Equal(1, paired.Decode.ByteOffset);
        Assert.Equal([0x00, 0x10, 0x20, 0x30], paired.States!.Select(s => s.Value));
        Assert.Equal(0, paired.Min);
        Assert.Equal(0x30, paired.Max);
        Assert.Empty(SignalCatalog.Check(paired));
        Assert.Equal("4L", paired.StateName(paired.Decode.Decode(Answer(5, 3))!.Value));

        var exported = MatchExport.ToPackEntries([match]);
        Assert.Contains("\"states\": [ { \"value\": 0, \"name\": \"2H\" }", exported, StringComparison.Ordinal);
        Assert.Contains("\"mask\": 48", exported, StringComparison.Ordinal);
    }

    // ── The synthetic truck ──────────────────────────────────────────────────

    [Fact]
    public void The_synthetic_truck_answers_the_states_by_name()
    {
        var idle = new SimulatedF150(Drives.ColdStartCity);
        idle.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, idle.Value("transmission.gearSelector"));
        Assert.Equal(0, idle.Value("drivetrain.4wdMode"));
        Assert.Equal(3, idle.Value("body.headlights"));

        var towing = new SimulatedF150(Drives.TowingPull);
        towing.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(3, towing.Value("transmission.gearSelector"));
        Assert.Equal(3, towing.Value("vehicle.driveMode"));

        idle.Cabin.FourWheelDrive = 3;
        Assert.Equal("4L", TestCatalog.Load()["drivetrain.4wdMode"].StateName(idle.Value("drivetrain.4wdMode")!.Value));
    }
}
