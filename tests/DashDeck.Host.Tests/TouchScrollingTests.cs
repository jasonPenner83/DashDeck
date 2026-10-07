using System.IO;
using System.Text.RegularExpressions;

namespace DashDeck.Host.Tests;

/// <summary>
/// Every list the dash scrolls can be dragged with a finger. A WPF <c>ScrollViewer</c> ignores touch
/// unless <c>PanningMode</c> is set, and every one in Settings shipped without it: the sensor list
/// could be scrolled with a mouse wheel at a desk and not at all on the tablet.
/// </summary>
public sealed partial class TouchScrollingTests
{
    private static string HostFolder([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "DashDeck.Host");

    [GeneratedRegex(@"<ScrollViewer\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex ScrollViewerTag();

    [Fact]
    public void Every_scroll_viewer_in_the_dash_pans_by_touch()
    {
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(HostFolder(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match tag in ScrollViewerTag().Matches(File.ReadAllText(file)))
            {
                // A text box's own content host scrolls its text, not a page.
                if (tag.Value.Contains("PART_ContentHost", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!tag.Value.Contains("PanningMode=", StringComparison.Ordinal))
                {
                    missing.Add($"{Path.GetFileName(file)}: {tag.Value[..Math.Min(80, tag.Value.Length)]}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Without PanningMode a finger cannot scroll these:\n" + string.Join("\n", missing));
    }
}
