using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DashDeck.Host;

/// <summary>
/// The shell window: status strip, six bands, navigation.
/// </summary>
/// <remarks>
/// Borderless and maximised, because the truck does not need a title bar — but explicitly
/// <i>not</i> kiosk mode, and nothing here takes the machine over. The Surface is a personal
/// device (constraint C1): Escape closes and gives it straight back.
/// <para>
/// Sizing is left entirely to the window manager. The shell is authored at exactly the
/// Pro 7's 912 × 1368 portrait size and a <c>Viewbox</c> fits that to whatever the window
/// turns out to be — 1:1 on the tablet in portrait, letterboxed on a landscape dev screen.
/// Computing the size by hand was tried and removed: mixing <c>SystemParameters.WorkArea</c>
/// with per-monitor DPI produced a window larger than the screen.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FillWorkArea();

        // The tablet gets rotated — that is the entire point of a portrait dash carried in
        // and out of a truck. Rotating changes the work area but not the window, so without
        // this the shell keeps the old landscape rectangle and stops filling the screen.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // A list dragged past its end by a finger makes Windows shove the whole window after it, to
        // show the edge — on a full-screen dash that reads as the dash coming loose. The list
        // stopping is feedback enough.
        ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
    }

    /// <summary>
    /// Take the whole work area, explicitly.
    /// </summary>
    /// <remarks>
    /// <c>WindowState="Maximized"</c> was tried first and rejected: a borderless window with
    /// no explicit bounds takes its restore size from its content, which here is the fixed
    /// 912 × 1368 design surface, and the result landed partly off-screen. Setting the
    /// rectangle outright is predictable on any DPI.
    /// </remarks>
    private void FillWorkArea()
    {
        var area = SystemParameters.WorkArea;

        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
    }

    /// <summary>
    /// Re-fit after a rotation or a resolution change.
    /// </summary>
    /// <remarks>
    /// Raised on a system thread, and Windows reports the new metrics slightly after the
    /// event, so this hops to the dispatcher at a low priority rather than measuring
    /// immediately and getting the old rectangle.
    /// </remarks>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, FillWorkArea);

    protected override void OnClosed(EventArgs e)
    {
        // SystemEvents is static: not detaching here keeps the window alive for the life of
        // the process.
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    /// <summary>
    /// Render the design surface to a PNG at its true 912 × 1368, whatever the window size.
    /// </summary>
    /// <remarks>
    /// A development affordance, not a product feature. Screen-grabbing the shell means
    /// fighting DPI virtualisation and whatever else has focus; rendering the visual tree
    /// directly gives an exact, repeatable image of the layout at tablet size from any
    /// machine. Useful for design review, and for noticing when a change moves something
    /// that was not supposed to move.
    /// </remarks>
    public void SaveDesignSurface(string path)
    {
        var bitmap = new RenderTargetBitmap(
            (int)DesignSurface.Width,
            (int)DesignSurface.Height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(DesignSurface);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Escape closes. A borderless, maximised window with no way out is a trap, and this
    /// one is launched by hand on a machine that is not dedicated to it.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
