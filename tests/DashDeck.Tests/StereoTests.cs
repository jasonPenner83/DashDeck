using DashDeck.Core.Audio;
using DashDeck.Core.Media;

namespace DashDeck.Tests;

/// <summary>The Stereo card's platform-neutral half: what plays next, and what gets out of the way.</summary>
public class StereoTests
{
    [Fact]
    public void Only_playable_files_become_tracks()
    {
        var tracks = MusicLibrary.From(
        [
            @"C:\Music\Album\01 One.mp3",
            @"C:\Music\Album\cover.jpg",
            @"C:\Music\Album\02 Two.flac",
            @"C:\Music\Album\notes.txt",
        ]);

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.True(MusicLibrary.IsPlayable(t.Path)));
    }

    [Fact]
    public void Tracks_sort_by_folder_then_by_name_ordinally()
    {
        // Ordinal, so a numbered run sorts the way it was numbered and the order does not shift
        // with the machine's locale.
        var tracks = MusicLibrary.From(
        [
            @"C:\Music\Beta\02 Second.mp3",
            @"C:\Music\Alpha\10 Ten.mp3",
            @"C:\Music\Beta\01 First.mp3",
            @"C:\Music\Alpha\02 Two.mp3",
        ]);

        Assert.Equal(["Alpha", "Alpha", "Beta", "Beta"], tracks.Select(t => t.Folder));
        Assert.Equal(["02 Two", "10 Ten", "01 First", "02 Second"], tracks.Select(t => t.Title));
    }

    [Fact]
    public void A_track_takes_its_title_from_the_file_and_its_album_from_the_folder()
    {
        var track = MusicLibrary.From([@"C:\Music\Rumours\04 Go Your Own Way.mp3"]).Single();

        Assert.Equal("04 Go Your Own Way", track.Title);
        Assert.Equal("Rumours", track.Folder);
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 2)]
    [InlineData(2, 3, 0)]
    public void Next_wraps_at_the_end(int current, int count, int expected) =>
        Assert.Equal(expected, MusicLibrary.Next(current, count));

    [Theory]
    [InlineData(2, 3, 1)]
    [InlineData(1, 3, 0)]
    [InlineData(0, 3, 2)]
    public void Previous_wraps_at_the_start(int current, int count, int expected) =>
        Assert.Equal(expected, MusicLibrary.Previous(current, count));

    [Fact]
    public void An_empty_queue_has_no_next_or_previous()
    {
        Assert.Equal(-1, MusicLibrary.Next(0, 0));
        Assert.Equal(-1, MusicLibrary.Previous(0, 0));
    }

    [Fact]
    public void Previous_restarts_the_track_once_it_is_past_the_first_few_seconds()
    {
        // The behaviour every player has and nobody writes down.
        Assert.Equal(PreviousAction.GoBack, MusicLibrary.PreviousFrom(TimeSpan.FromSeconds(1)));
        Assert.Equal(PreviousAction.Restart, MusicLibrary.PreviousFrom(TimeSpan.FromSeconds(30)));
    }

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
