using System.Windows;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// Android on the stage — reached by double-tapping the nav's ANDROID cell (ADR-0075).
/// </summary>
/// <remarks>
/// Shares the one <see cref="AndroidViewModel"/> with the full-screen region, as the map does: the
/// emulator is one device in one place at a time, and moving it must not restart it. The view is a
/// plain WPF picture, so the overflow menu draws over it with nothing to stand down for.
/// </remarks>
/// <param name="android">The shared view-model.</param>
/// <param name="fullScreen">Take it off the stage and open it full screen — what a tap on ANDROID does.</param>
public sealed class AndroidStageOccupant(AndroidViewModel android, Action fullScreen) : IStageOccupant
{
    /// <inheritdoc />
    public string Name => "ANDROID";

    /// <inheritdoc />
    public FrameworkElement CreateView() => new Views.AndroidPanel { DataContext = android };

    /// <inheritdoc />
    public IReadOnlyList<StageAction> Actions =>
    [
        new StageAction("ANDROID FULL SCREEN", fullScreen),
        new StageAction("STOP ANDROID", () => android.StopCommand.Execute(null)),
    ];

    /// <inheritdoc />
    public string Describe() => android.Describe();

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to release: the emulator belongs to the view-model, which outlives the placement, and
    /// leaving the stage must not stop Android. The panel's own unload stops its frames.
    /// </remarks>
    public void Dispose()
    {
    }
}
