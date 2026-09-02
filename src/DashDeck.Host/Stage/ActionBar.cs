using System.Windows;
using System.Windows.Controls;

namespace DashDeck.Host.Stage;

/// <summary>
/// Builds an occupant's action bar.
/// </summary>
/// <remarks>
/// Constructed in code rather than as a XAML file per occupant. Each bar is a handful of
/// buttons and at most one slider, and three near-identical <c>UserControl</c>s would be more
/// markup to keep in step than the thing they describe — the styles still come from the
/// design system, so a bar changes colour with everything else.
/// <para>
/// One band tall (195), which is what leaves an occupant with a bar exactly the 513 of
/// content a three-band stage used to give it.
/// </para>
/// </remarks>
public static class ActionBar
{
    /// <summary>How tall an action bar is: one band.</summary>
    public const double Height = BandGrid.BandHeight;

    /// <summary>A row of controls, laid out left to right and vertically centred.</summary>
    public static FrameworkElement Row(params UIElement[] controls)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return new Border
        {
            Padding = new Thickness(BandGrid.SideMargin, 0, BandGrid.SideMargin, 0),
            Child = panel,
        };
    }

    /// <summary>
    /// A button sized for a moving vehicle.
    /// </summary>
    /// <remarks>
    /// Reuses the launcher's style, which is 56 tall with a 104 minimum width — already
    /// derived for a thumb on a bumpy road, so a second set of numbers would only be a second
    /// set of numbers to get wrong.
    /// </remarks>
    public static Button Button(string caption, Action onClick, double width = double.NaN)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("StageLauncherButton"),
            Content = new TextBlock
            {
                Text = caption,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
                FontSize = 15,
            },
        };

        if (!double.IsNaN(width))
        {
            button.Width = width;
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A caption, for the quiet half of a bar.</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("CaptionText"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 0),
    };
}
