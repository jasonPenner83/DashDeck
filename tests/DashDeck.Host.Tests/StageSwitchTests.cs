using DashDeck.Host.Stage;

namespace DashDeck.Host.Tests;

/// <summary>
/// The rule for what choosing a new occupant does to the two stage slots (ADR-0026). Pulled out
/// of the view model precisely so it can be tested without the whole pipeline (F7).
/// </summary>
public sealed class StageSwitchTests
{
    [Theory]
    // Picking the source already playing in the background always brings it forward — even with
    // keep-audio off, because it is already alive and you asked for it.
    [InlineData(true, false, true, true, StageSwitchAction.BringForwardSource)]
    [InlineData(false, false, true, true, StageSwitchAction.BringForwardSource)]
    // A different source always replaces: one source at a time.
    [InlineData(true, false, true, false, StageSwitchAction.ReplaceWithSource)]
    [InlineData(true, true, true, false, StageSwitchAction.ReplaceWithSource)]
    [InlineData(false, true, true, false, StageSwitchAction.ReplaceWithSource)]
    // Keep-audio on, a source in front, a silent occupant chosen → the source goes to background.
    [InlineData(true, true, false, false, StageSwitchAction.BackgroundSourceShowIncoming)]
    // Keep-audio off, a source in front, a silent occupant chosen → the source is stopped.
    [InlineData(false, true, false, false, StageSwitchAction.ReplaceFrontKeepBackground)]
    // A silent occupant in front, another silent one chosen → swap the front, keep any background.
    [InlineData(true, false, false, false, StageSwitchAction.ReplaceFrontKeepBackground)]
    [InlineData(false, false, false, false, StageSwitchAction.ReplaceFrontKeepBackground)]
    public void Decide_maps_state_to_action(
        bool keepAudio,
        bool frontIsSource,
        bool incomingIsSource,
        bool incomingIsBackgroundSource,
        StageSwitchAction expected) =>
        Assert.Equal(
            expected,
            StageSwitch.Decide(keepAudio, frontIsSource, incomingIsSource, incomingIsBackgroundSource));

    [Fact]
    public void A_stage_option_carries_its_source_flag()
    {
        Assert.True(new StageOption("X", "d", null, StageKind.Web, AudioVisualSource: true).AudioVisualSource);
        Assert.False(new StageOption("Y", "d", null, StageKind.Screen).AudioVisualSource);
    }
}
