using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Views;

/// <summary>The Stereo panel on the bottom third (Q14).</summary>
public partial class StereoPanel : UserControl
{
    public StereoPanel() => InitializeComponent();

    /// <summary>
    /// Seek by tapping the position line.
    /// </summary>
    /// <remarks>
    /// A tap rather than a dragged thumb: a thumb is a small target in a moving truck, and a
    /// slider bound two-way would fight the once-a-second refresh that advances the bar.
    /// </remarks>
    private void SeekStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement strip || DataContext is not StereoViewModel model)
        {
            return;
        }

        var width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        model.SeekToFraction(e.GetPosition(strip).X / width);
    }
}
