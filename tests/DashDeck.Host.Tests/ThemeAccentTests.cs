using System.IO;
using System.Windows.Media;
using DashDeck.Host.Theme;

namespace DashDeck.Host.Tests;

/// <summary>A theme's accent obeys the same rule a hand-picked one does (ADR-0014, ADR-0036).</summary>
public sealed class ThemeAccentTests
{
    private static string ShippedFolder([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "catalog", "themes");

    [Fact]
    public void Every_shipped_themes_accent_passes_the_accent_check()
    {
        var library = new ThemeLibrary(ShippedFolder(), Path.Combine(Path.GetTempPath(), "dashdeck-none-" + Guid.NewGuid().ToString("N")));

        Assert.Contains(library.Themes, t => t.Origin is ThemeOrigin.Shipped);

        foreach (var theme in library.Themes)
        {
            var accent = ThemeResolver.Resolve(theme, night: false).Colour("accent");
            var check = AccentValidation.Check(Color.FromRgb(accent.R, accent.G, accent.B));
            Assert.True(check.IsUsable, $"{theme.Name}: {check.Message}");
        }
    }

    [Fact]
    public void The_classic_lcars_orange_would_be_refused_which_is_why_it_is_not_used()
    {
        // #FF9900 sits about 7° from the Stale amber.
        Assert.False(AccentValidation.Check(Color.FromRgb(0xFF, 0x99, 0x00)).IsUsable);
    }
}
