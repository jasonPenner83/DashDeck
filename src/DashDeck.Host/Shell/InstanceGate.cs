namespace DashDeck.Host.Shell;

/// <summary>What a starting DashDeck does about another one already running.</summary>
public enum InstanceStep
{
    /// <summary>Nobody else holds the dash: start.</summary>
    Run,

    /// <summary>Another dash is up and showing: bring it to the front and leave.</summary>
    ActivateOtherAndExit,

    /// <summary>Another dash is still starting, or still closing: look again shortly.</summary>
    Wait,

    /// <summary>Waited long enough and it never showed or let go: leave quietly.</summary>
    GiveUp,
}

/// <summary>
/// One DashDeck at a time, decided — kept free of Windows so it can be tested anywhere.
/// </summary>
/// <remarks>
/// <b>Why it matters on the truck:</b> the adapter is a serial port only one process can hold. A
/// second copy cannot open it and comes up on no truck, and a touch screen makes a second copy
/// easy: DashDeck spends a few seconds finding the adapter before its window appears, so a second
/// tap on the icon looks like the first one did nothing.
/// <para>
/// The running one holds a named mutex from launch until its vehicle stack has let go of the port.
/// A newcomer that cannot take it looks for the other's window: shown means the newcomer brings it
/// forward and leaves; not shown means the other is either still starting or closing, so it waits.
/// A closing one lets go within its exit backstop (10 s) and the newcomer then starts — which is
/// also how RESTART NOW hands over. A starting one shows its window and the newcomer brings it
/// forward. Neither within <see cref="Patience"/> and the newcomer leaves rather than fight.
/// </para>
/// </remarks>
public static class InstanceGate
{
    /// <summary>How long a newcomer waits for the other to show or let go.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>How often it looks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>The next step, from what was just seen.</summary>
    /// <param name="acquired">This process now holds the mutex.</param>
    /// <param name="otherWindowShown">Another DashDeck has a visible main window.</param>
    /// <param name="waited">How long this process has been waiting so far.</param>
    public static InstanceStep Next(bool acquired, bool otherWindowShown, TimeSpan waited)
    {
        if (acquired)
        {
            return InstanceStep.Run;
        }

        if (otherWindowShown)
        {
            return InstanceStep.ActivateOtherAndExit;
        }

        return waited >= Patience ? InstanceStep.GiveUp : InstanceStep.Wait;
    }
}
