namespace DashDeck.Host.Shell;

/// <summary>
/// Closing DashDeck from the menu takes two taps: the first arms it, the second within a few
/// seconds closes.
/// </summary>
/// <remarks>
/// On the truck there is no keyboard, so the menu is the only way out — and the one tap that
/// ends the dash should not be the one a bump in the road lands. The window is short so an
/// armed button left alone goes back to harmless by itself.
/// </remarks>
public sealed class CloseConfirm(TimeSpan window)
{
    private DateTimeOffset? _armedUntil;

    /// <summary>How long the second tap has.</summary>
    public TimeSpan Window { get; } = window;

    /// <summary>True while a second tap would close.</summary>
    public bool IsArmed(DateTimeOffset now) => _armedUntil is { } until && now < until;

    /// <summary>A tap. True when this one should close the app.</summary>
    public bool Tap(DateTimeOffset now)
    {
        if (IsArmed(now))
        {
            _armedUntil = null;
            return true;
        }

        _armedUntil = now + Window;
        return false;
    }

    /// <summary>Back to harmless — the menu was dismissed.</summary>
    public void Disarm() => _armedUntil = null;
}
