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
        DataContext = _model;
        Closed += (_, _) => _model.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;
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
