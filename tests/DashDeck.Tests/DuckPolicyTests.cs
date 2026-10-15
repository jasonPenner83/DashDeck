using DashDeck.Core.Audio;

namespace DashDeck.Tests;

/// <summary>
/// The ducking contract (Q14, ADR-0060): what DashDeck's own playback does when something else
/// wants the speakers.
/// </summary>
/// <remarks>
/// The panel and the player these were written for are parked on <c>parked/stereo-music</c>, and
/// these stay: the policy is pure, it is the contract Q14 asked for, and whatever plays audio next
/// needs the same answers. Only the music-library tests went with the branch.
/// </remarks>
public class DuckPolicyTests
{
    [Fact]
    public void Nothing_else_playing_leaves_the_music_alone()
    {
        var plan = DuckPolicy.For(AudioFocus.None);

        Assert.Equal(1.0, plan.VolumeScale);
        Assert.False(plan.Pause);
    }

    [Fact]
    public void A_spoken_instruction_lowers_the_music_rather_than_pausing_it()
    {
        // Q14's whole point: Windows mixes projection's audio with DashDeck's, so without this a
        // turn instruction arrives underneath a chorus. Pausing would leave a hole longer than
        // the words.
        var plan = DuckPolicy.For(AudioFocus.SpokenInstruction);

        Assert.False(plan.Pause);
        Assert.True(plan.VolumeScale > 0, "silence is not ducking");
        Assert.True(plan.VolumeScale < 0.5, "not lowered enough to hear words over");
    }

    [Fact]
    public void The_phone_playing_its_own_music_stops_ours_outright()
    {
        // Two music sources mixed is noise with no useful reading, so this one is a pause rather
        // than a duck.
        var plan = DuckPolicy.For(AudioFocus.ProjectedMedia);

        Assert.True(plan.Pause);
        Assert.Equal(0.0, plan.VolumeScale);
    }

    [Fact]
    public void A_chime_ducks_harder_than_speech()
    {
        Assert.True(
            DuckPolicy.For(AudioFocus.WarningChime).VolumeScale
                < DuckPolicy.For(AudioFocus.SpokenInstruction).VolumeScale,
            "a missed warning is the one failure that matters");
    }

    [Fact]
    public void The_strongest_claim_wins_when_several_hold_at_once()
    {
        // Taking the strongest rather than the latest means music cannot be quietly restored by a
        // weak claim ending while a stronger one still holds.
        var plan = DuckPolicy.For(
            [AudioFocus.SpokenInstruction, AudioFocus.ProjectedMedia, AudioFocus.None]);

        Assert.True(plan.Pause);

        var quieter = DuckPolicy.For([AudioFocus.SpokenInstruction, AudioFocus.WarningChime]);
        Assert.Equal(DuckPolicy.ChimeScale, quieter.VolumeScale);
        Assert.False(quieter.Pause);
    }

    [Fact]
    public void No_claims_at_all_is_the_same_as_nothing_playing()
    {
        var plan = DuckPolicy.For([]);

        Assert.Equal(1.0, plan.VolumeScale);
        Assert.False(plan.Pause);
    }
}
