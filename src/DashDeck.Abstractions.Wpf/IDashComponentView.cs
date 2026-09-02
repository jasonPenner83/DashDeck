using System.Windows;

namespace DashDeck.Abstractions.Wpf;

/// <summary>
/// The UI half of a component. Implemented only by components that draw something.
/// </summary>
/// <remarks>
/// A component is two parts, and only the first is required:
/// <list type="number">
///   <item>
///     A <b>widget</b>, which lives in a band on the home screen. Every visual component
///     has one.
///   </item>
///   <item>
///     An <b>optional full-screen view</b>, opened when the widget is tapped. A component
///     that has nothing more to show than its widget simply does not override
///     <see cref="CreateFullScreen"/>, and the host makes its widget non-interactive
///     rather than opening an empty screen.
///   </item>
/// </list>
/// <para>
/// This interface lives in its own <c>net10.0-windows</c> assembly on purpose (ADR-0010).
/// A UI type in <c>DashDeck.Abstractions</c> would drag WPF into every component and make
/// the engine unbuildable off Windows, which is what let P0 be finished before any Windows
/// machine was involved.
/// </para>
/// <para>
/// The host receives a <see cref="FrameworkElement"/> — a view, not a view-model — so it
/// cannot impose a UI pattern on components. The shell itself is MVVM; components are
/// advised to be, and <see cref="ObservableSignal"/> exists to make that the easy path.
/// </para>
/// </remarks>
public interface IDashComponentView
{
    /// <summary>
    /// Build the widget shown in the home screen's band grid.
    /// </summary>
    /// <remarks>
    /// Sized by the host to the widget size declared in <c>component.json</c>. Do not
    /// assume pixel dimensions; the band grid is the authority and the tablet's geometry
    /// can change under you.
    /// <para>
    /// Called on the UI thread. Must not block: everything vehicle-related is already
    /// asynchronous, and a dash that freezes while someone is driving is the one failure
    /// mode this project will not accept.
    /// </para>
    /// </remarks>
    FrameworkElement CreateWidget();

    /// <summary>
    /// Build the full-screen view opened when the widget is tapped, or return
    /// <see langword="null"/> if this component has none.
    /// </summary>
    /// <remarks>
    /// Optional by design — the default returns <see langword="null"/>, so a component with
    /// only a widget implements nothing here. History, detail and settings-like surfaces
    /// belong here rather than crammed into a widget that has to stay glanceable.
    /// </remarks>
    FrameworkElement? CreateFullScreen() => null;
}
