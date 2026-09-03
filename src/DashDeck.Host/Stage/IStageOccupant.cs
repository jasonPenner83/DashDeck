using System.Windows;

namespace DashDeck.Host.Stage;

/// <summary>One thing an occupant can be told to do.</summary>
/// <param name="Caption">Short, uppercase, and a verb.</param>
/// <param name="Invoke">What it does.</param>
public sealed record StageAction(string Caption, Action Invoke);

/// <summary>
/// Something that fills the top bands of the home screen.
/// </summary>
/// <remarks>
/// Deliberately <b>internal to the host for now</b>. The stage is a layer with its own
/// lifecycle (B2), and the contract by which a component claims it is still settling — so
/// this stays a host-side shape until there is an ADR to make it public. Publishing it early
/// would ossify a guess, and the SDK's whole value is that it is small and stable.
/// <para>
/// It mirrors <see cref="Abstractions.Wpf.IDashComponentView"/> on purpose: an occupant hands
/// back a view, not a view-model, so the host cannot dictate how it is built.
/// </para>
/// </remarks>
public interface IStageOccupant : IDisposable
{
    /// <summary>Short uppercase name for the launcher, e.g. <c>VIDEO</c>.</summary>
    string Name { get; }

    /// <summary>Build the view. Called on the UI thread, once.</summary>
    FrameworkElement CreateView();

    /// <summary>
    /// Controls this occupant offers, shown in the overflow menu.
    /// </summary>
    /// <remarks>
    /// Actions rather than a view. A dedicated one-band row cost the occupant a quarter of
    /// the stage to carry two buttons, which on the road bought a control you use once and
    /// lost picture you look at constantly.
    /// <para>
    /// Handing back verbs rather than a <c>FrameworkElement</c> also means the shell decides
    /// where they live, so moving them again costs nothing here. The airspace argument that
    /// forced a real row still holds for anything drawn <em>over</em> an occupant — the menu
    /// is a separate full-screen layer, so it is unaffected.
    /// </para>
    /// </remarks>
    IReadOnlyList<StageAction> Actions => [];

    /// <summary>
    /// What this occupant is actually doing, in one line.
    /// </summary>
    /// <remarks>
    /// A screenshot cannot tell playing from broken when the picture lives in a child window,
    /// so an occupant has to be able to say. Overriding it is optional; the default at least
    /// names itself.
    /// </remarks>
    string Describe() => Name;
}
