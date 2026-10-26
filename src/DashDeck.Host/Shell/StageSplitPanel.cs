using System.Windows;
using System.Windows.Controls;
using DashDeck.Core.Screen;

namespace DashDeck.Host.Shell;

/// <summary>
/// The body between the status strip and the nav: the stage and the region, one above the other
/// upright and side by side on its side (ADR-0077).
/// </summary>
/// <remarks>
/// Two children, in order: the stage, then the region. Where each goes is
/// <see cref="ScreenShape.Split"/>'s; this only measures and arranges them there. With no stage —
/// a full-screen view — the stage is given nothing, as a nought-band grid row gave it before, so its
/// occupant keeps running behind the region instead of being taken down.
/// </remarks>
public sealed class StageSplitPanel : Panel
{
    public static readonly DependencyProperty IsLandscapeProperty = DependencyProperty.Register(
        nameof(IsLandscape), typeof(bool), typeof(StageSplitPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StageBandsProperty = DependencyProperty.Register(
        nameof(StageBands), typeof(int), typeof(StageSplitPanel),
        new FrameworkPropertyMetadata(BandGrid.StageBands, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Side by side rather than one above the other.</summary>
    public bool IsLandscape
    {
        get => (bool)GetValue(IsLandscapeProperty);
        set => SetValue(IsLandscapeProperty, value);
    }

    /// <summary>
    /// The stage's bands upright — four, or none while a full-screen view has the whole body. On
    /// its side only none or some matters: the stage is the body's full height.
    /// </summary>
    public int StageBands
    {
        get => (int)GetValue(StageBandsProperty);
        set => SetValue(StageBandsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (stage, region) = Boxes(availableSize);
        Child(0)?.Measure(new Size(stage.Width, stage.Height));
        Child(1)?.Measure(new Size(region.Width, region.Height));
        return new Size(Finite(availableSize.Width), Finite(availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (stage, region) = Boxes(finalSize);
        Child(0)?.Arrange(new Rect(stage.X, stage.Y, stage.Width, stage.Height));
        Child(1)?.Arrange(new Rect(region.X, region.Y, region.Width, region.Height));
        return finalSize;
    }

    private (Box Stage, Box Region) Boxes(Size size) =>
        ScreenShape.Split(IsLandscape, StageBands > 0, Finite(size.Width), Finite(size.Height),
            BandGrid.Height(StageBands), BandGrid.LandscapeStageWidth);

    private UIElement? Child(int index) => InternalChildren.Count > index ? InternalChildren[index] : null;

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
