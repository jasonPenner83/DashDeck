namespace DashDeck.Abstractions;

/// <summary>Severity of a component log line.</summary>
public enum LogLevel
{
    /// <summary>Detail useful only while developing the component.</summary>
    Debug,

    /// <summary>Ordinary progress worth a line.</summary>
    Info,

    /// <summary>Something is off but the component carried on.</summary>
    Warning,

    /// <summary>The component could not do what was asked.</summary>
    Error,
}

/// <summary>
/// Where a component's diagnostics go.
/// </summary>
/// <remarks>
/// A component never owns the sink — the host does, and tags every line with the
/// component's id so a misbehaving one is identifiable in a shared log. Deliberately tiny:
/// a full logging framework in <c>DashDeck.Abstractions</c> would be a dependency shared
/// across every load context, which the assembly exists to avoid (ADR-0002, ADR-0010).
/// </remarks>
public interface IComponentLogger
{
    /// <summary>Write one line at the given level.</summary>
    void Log(LogLevel level, string message, Exception? exception = null);
}
