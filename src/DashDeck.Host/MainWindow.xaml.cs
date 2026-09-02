using System.Windows;
using System.Windows.Input;

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
