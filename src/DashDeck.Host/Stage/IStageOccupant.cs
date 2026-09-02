using System.Windows;

namespace DashDeck.Host.Stage;

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
    /// Build the occupant's action bar, or return <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// One band tall, between the occupant and the launcher. This is the answer to F8: every
    /// occupant so far renders into a child window — LibVLC's video surface, WebView2's
    /// browser — and a child window draws over <em>all</em> WPF content whatever the z-order
    /// says, so controls floating on top of an occupant are invisible and untappable the
    /// moment anything loads. A real row beside it is the only arrangement that survives.
    /// <para>
    /// An occupant with a bar gets 513 of content, which is exactly what a three-band stage
    /// used to give it — so video keeps its picture size and gains its transport controls
    /// rather than trading one for the other.
    /// </para>
    /// </remarks>
    FrameworkElement? CreateActionBar() => null;

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
