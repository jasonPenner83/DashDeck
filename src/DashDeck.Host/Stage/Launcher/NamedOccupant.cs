using System.Windows;

namespace DashDeck.Host.Stage.Launcher;

/// <summary>
/// An occupant shown under the name its launcher entry gives it (ADR-0038).
/// </summary>
/// <remarks>
/// The clock calls itself CLOCK; a launcher file may call it TIME. The shell matches the button
/// to what is on the stage by name, and the status strip shows that name, so the entry's name has
/// to be the one the occupant answers to. Everything else passes straight through — this changes
/// what it is called, never what it does.
/// </remarks>
public sealed class NamedOccupant(string name, IStageOccupant inner) : IStageOccupant
{
    /// <summary>Wrap only when the names differ, so the common case is the occupant itself.</summary>
    public static IStageOccupant? As(string name, IStageOccupant? occupant) =>
        occupant is null || occupant.Name == name ? occupant : new NamedOccupant(name, occupant);

    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public FrameworkElement CreateView() => inner.CreateView();

    /// <inheritdoc />
    public IReadOnlyList<StageAction> Actions => inner.Actions;

    /// <inheritdoc />
    public string Describe() => inner.Describe();

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();
}
