namespace DashDeck.Abstractions;

/// <summary>
/// How important a signal is when demand exceeds what the adapter can deliver.
/// </summary>
/// <remarks>
/// The adapter is a single serialised resource with a finite request budget. When total
/// demand exceeds it, the arbiter satisfies higher priorities first and degrades lower
/// ones — so this value decides what survives contention, not how fast anything runs.
/// </remarks>
public enum SignalPriority
{
    /// <summary>Wanted only if nothing else needs the budget. Components that are not visible land here.</summary>
    Background = 0,

    /// <summary>Nice to have. Ambient temperature, odometer — things that change slowly.</summary>
    Low = 1,

    /// <summary>The default.</summary>
    Normal = 2,

    /// <summary>Degrade last. Reserve it for signals whose absence makes a component useless.</summary>
    High = 3,
}
