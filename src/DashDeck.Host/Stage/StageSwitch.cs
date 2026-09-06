namespace DashDeck.Host.Stage;

/// <summary>What choosing a new occupant does to the two stage slots.</summary>
public enum StageSwitchAction
{
    /// <summary>The chosen option is the source already playing in the background: show it again,
    /// disposing the non-source that was in front. The source is never recreated — that is what
    /// keeps its position and its audio.</summary>
    BringForwardSource,

    /// <summary>Keep the audio/video source that was in front alive but hidden, and show the
    /// chosen (non-source) occupant over it. The background-audio case.</summary>
    BackgroundSourceShowIncoming,

    /// <summary>The chosen occupant is a source: dispose whatever is in front and any source that
    /// was in the background, and bring the new source up. One source at a time.</summary>
    ReplaceWithSource,

    /// <summary>Dispose only the occupant in front and show the chosen (non-source) one; a source
    /// already in the background keeps playing.</summary>
    ReplaceFrontKeepBackground,
}

/// <summary>
/// The one rule for what happens when a new occupant is chosen, given the persistent-source
/// setting (ADR-0026, amending ADR-0025).
/// </summary>
/// <remarks>
/// Pulled out of <c>ShellViewModel</c> as a pure function precisely because the view model cannot
/// be unit-tested without the whole vehicle pipeline and a WPF surface (F7). The tricky part of
/// the feature is this truth table, so it is the part that gets tested.
/// </remarks>
public static class StageSwitch
{
    /// <param name="keepAudio">The global "keep stage audio playing when you switch" setting.</param>
    /// <param name="frontIsSource">Whether the occupant currently in front is an audio/video source.</param>
    /// <param name="incomingIsSource">Whether the chosen occupant is an audio/video source.</param>
    /// <param name="incomingIsBackgroundSource">
    /// Whether the chosen option <em>is</em> the source currently playing in the background — the
    /// case that must bring it forward rather than start a second copy.
    /// </param>
    public static StageSwitchAction Decide(
        bool keepAudio,
        bool frontIsSource,
        bool incomingIsSource,
        bool incomingIsBackgroundSource)
    {
        if (incomingIsBackgroundSource)
        {
            return StageSwitchAction.BringForwardSource;
        }

        if (incomingIsSource)
        {
            return StageSwitchAction.ReplaceWithSource;
        }

        if (keepAudio && frontIsSource)
        {
            return StageSwitchAction.BackgroundSourceShowIncoming;
        }

        return StageSwitchAction.ReplaceFrontKeepBackground;
    }
}
