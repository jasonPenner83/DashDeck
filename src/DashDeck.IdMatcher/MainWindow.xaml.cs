using System.Windows;
using System.Windows.Input;

namespace DashDeck.IdMatcher;

/// <summary>The matcher's one window. Keyboard first: F2 and F3 jump to the two boxes you type in.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        DataContext = _model;
        Closed += (_, _) => _model.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Fit the window inside the screen's work area, title bar and all.
    /// </summary>
    /// <remarks>
    /// The design size is for a large monitor. A laptop at 125–150% scaling has well under 1480 × 920
    /// to give, and Windows placed the window with its title bar off the top, so it could neither be
    /// moved, maximized nor closed with the mouse. So: the design size or the work area, whichever is
    /// smaller, centred in it — and maximized outright when the screen is small enough that the
    /// three panels need all of it.
    /// </remarks>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;

        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = area.Left + ((area.Width - Width) / 2);
        Top = area.Top + ((area.Height - Height) / 2);

        if (area.Width < 1480 || area.Height < 920)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                NameBox.Focus();
                NameBox.SelectAll();
                e.Handled = true;
                break;

            case Key.F3:
                ShownBox.Focus();
                ShownBox.SelectAll();
                e.Handled = true;
                break;
        }
    }
}
