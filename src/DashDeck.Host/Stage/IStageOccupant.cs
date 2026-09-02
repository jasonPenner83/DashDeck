using System.Windows;

namespace DashDeck.Host.Stage;

/// <summary>
/// Something that fills the top bands of the home screen.
/// </summary>
/// <remarks>
/// Deliberately <b>internal to the host for now</b>. The stage is a layer with its own
/// lifecycle (B2), and the contract by which a component claims bands is still undecided —
/// so this stays a host-side shape until there is an ADR to make it public. Publishing it
/// early would ossify a guess, and the SDK's whole value is that it is small and stable.
/// <para>
/// It mirrors <see cref="Abstractions.Wpf.IDashComponentView"/> on purpose: an occupant
/// hands back a view, not a view-model, so the host cannot dictate how it is built.
/// </para>
/// </remarks>
public interface IStageOccupant : IDisposable
{
    /// <summary>Short uppercase name for the stage chip, e.g. <c>VIDEO</c>.</summary>
    string Name { get; }

    /// <summary>
    /// How many of the six bands this occupant wants.
    /// </summary>
    /// <remarks>
    /// Video asks for three: 16:9 at 912 wide needs 513px, and three bands is 555, so it
    /// fits with a thin letterbox rather than an awkward crop. That the number falls out of
    /// the grid rather than being chosen is the point of the band system.
    /// </remarks>
    int PreferredBands { get; }

    /// <summary>Build the view. Called on the UI thread, once.</summary>
    FrameworkElement CreateView();

    /// <summary>
    /// What this occupant is actually doing, in one line.
    /// </summary>
    /// <remarks>
    /// Every occupant so far renders into a child window — LibVLC's video surface and
    /// WebView2 both — which means <c>RenderTargetBitmap</c> sees a black rectangle whether
    /// they are working or not. A screenshot cannot tell playing from broken, so an occupant
    /// has to be able to say. Overriding it is optional; the default at least names itself.
    /// </remarks>
    string Describe() => Name;
}
