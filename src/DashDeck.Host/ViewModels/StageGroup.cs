namespace DashDeck.Host.ViewModels;

/// <summary>
/// One headed section of the stage picker — SCREENS, WEB or APPS — and the options under it.
/// </summary>
/// <remarks>
/// A plain carrier so the picker can render a header plus a three-across grid per section. The
/// grouping is done in the shell rather than by a <c>CollectionView</c>'s <c>GroupStyle</c>,
/// which laid the sections side by side instead of stacked.
/// </remarks>
public sealed record StageGroup(string Header, IReadOnlyList<StageOptionViewModel> Options);
