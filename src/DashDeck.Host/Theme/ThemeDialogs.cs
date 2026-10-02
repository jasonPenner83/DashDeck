using System.Diagnostics;
using DashDeck.Host.ViewModels;
using Microsoft.Win32;

namespace DashDeck.Host.Theme;

/// <summary>The Windows file pickers behind IMPORT, EXPORT and OPEN FOLDER (ADR-0036).</summary>
internal sealed class ThemeDialogs : IThemeDialogs
{
    public string? PickThemeFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a DashDeck theme",
            Filter = "DashDeck theme (*.json)|*.json",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Export the theme into…" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
}
