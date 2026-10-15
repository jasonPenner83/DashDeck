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
    /// where they live, so moving them again costs nothing here.
    /// </para>
    /// <para>
    /// <b>This used to claim the menu was unaffected by airspace, and that was wrong.</b> It
    /// holds for an occupant the shell draws, and not at all for one that is a window of its
    /// own: an owned window sits above its owner by definition (ADR-0021), so the menu opens
    /// <em>underneath</em> a hosted application and its own actions cannot be reached. Found in
    /// the truck, with Nuvio on the stage. <see cref="SetObscured"/> is the answer.
    /// </para>
    /// </remarks>
    IReadOnlyList<StageAction> Actions => [];

    /// <summary>
    /// Told that the shell is drawing something over this occupant, or has stopped.
    /// </summary>
    /// <remarks>
    /// Only occupants that are a window of their own need to act on it, which is why the default
    /// does nothing: the shell draws over everything else correctly. A hosted application cannot
    /// be drawn over at all, so while the menu is up it stands down and comes back when the menu
    /// closes — the alternative is a menu whose contents are invisible, which is what shipped.
    /// <para>
    /// Deliberately not the same thing as the picker, a full-screen view or the warning window:
    /// those already collapse the anchor, which the host reads as "not on screen". This is for
    /// something drawn <em>over</em> the stage while the stage is still laid out.
    /// </para>
    /// </remarks>
    void SetObscured(bool obscured)
    {
    }

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
