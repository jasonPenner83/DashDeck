namespace DashDeck.Abstractions;

/// <summary>
/// Time, as an injected dependency.
/// </summary>
/// <remarks>
/// Nothing in DashDeck reads <c>DateTimeOffset.Now</c> directly. Time is data here: a
/// component that reads the wall clock cannot be replayed against a recorded drive or
/// tested against a scripted one, and both are how this project is developed.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. Used everywhere except replay and tests.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
