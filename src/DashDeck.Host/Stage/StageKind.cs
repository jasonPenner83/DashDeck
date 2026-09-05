namespace DashDeck.Host.Stage;

/// <summary>
/// What kind of thing an occupant is, for grouping the picker.
/// </summary>
/// <remarks>
/// The honest dividing line is not "ours versus theirs" but <b>where it renders</b>: a
/// <see cref="Screen"/> and a <see cref="Web"/> page both live inside DashDeck's own window,
/// while an <see cref="App"/> is a separate Windows process owned and placed over the stage
/// (ADR-0020/0021) or, when adoption will not take, left in its own window. Web is split from
/// Screen because a browser page of an external service reads differently from a gauge cluster,
/// even though both are drawn by us.
/// </remarks>
public enum StageKind
{
    /// <summary>Drawn by DashDeck itself — gauges, clock, compass, phone projection, video.</summary>
    Screen,

    /// <summary>A page in WebView2 — maps, and the web music players.</summary>
    Web,

    /// <summary>A separate Windows program, launched and adopted where that works.</summary>
    App,
}
