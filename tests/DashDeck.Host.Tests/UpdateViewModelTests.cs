using DashDeck.Host.Updates;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// The update screen's manners (ADR-0061). The requirement that mattered most was a negative one
/// — it must never check, download or nag on its own — so that is what is pinned here.
/// </summary>
public sealed class UpdateViewModelTests
{
    private const string Released = """
        {
          "tag_name": "v.0.10.0",
          "html_url": "https://github.com/jasonPenner83/DashDeck/releases/tag/v.0.10.0",
          "published_at": "2026-10-13T12:00:00Z",
          "assets": [
            {
              "name": "DashDeck-v0.10.0-win-x64.zip",
              "browser_download_url": "https://example.invalid/DashDeck-v0.10.0-win-x64.zip",
              "size": 350224384
            }
          ]
        }
        """;

    private static (UpdateViewModel Screen, Func<int> Asked) Build(string reply = Released, string running = "0.9.0")
    {
        var asked = 0;

        var service = new UpdateService(
            running,
            @"C:\Users\ABC\Documents\app\dist\DashDeck",
            (_, _) =>
            {
                asked++;
                return Task.FromResult(reply);
            });

        return (new UpdateViewModel(service, () => new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)), () => asked);
    }

    [Fact]
    public void Opening_the_screen_asks_nothing()
    {
        // The whole point. A dash that checks on its own eventually interrupts a drive to mention
        // a release, and a 334 MB download that starts itself does it on a phone hotspot.
        var (screen, asked) = Build();

        Assert.Equal(0, asked());
        Assert.False(screen.CanDownload);
        Assert.False(screen.CanInstall);
        Assert.Contains("0.9.0", screen.Headline, StringComparison.Ordinal);
        Assert.Contains("never looks on its own", screen.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checking_asks_once_and_offers_the_download()
    {
        var (screen, asked) = Build();

        await screen.CheckCommand.ExecuteAsync(null);

        Assert.Equal(1, asked());
        Assert.Contains("0.10.0 is available", screen.Headline, StringComparison.Ordinal);
        Assert.Contains("334 MB", screen.Headline, StringComparison.Ordinal);
        Assert.True(screen.CanDownload);
        Assert.True(screen.HasNotes);

        // Finding an update is not the same as having one, and the screen says so.
        Assert.False(screen.CanInstall);
        Assert.Contains("second button", screen.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_current_build_offers_nothing_to_press()
    {
        var (screen, _) = Build(running: "0.10.0");

        await screen.CheckCommand.ExecuteAsync(null);

        Assert.Contains("nothing newer", screen.Headline, StringComparison.Ordinal);
        Assert.False(screen.CanDownload);
        Assert.False(screen.CanInstall);
    }

    [Fact]
    public async Task A_build_that_cannot_name_itself_says_so_rather_than_offering_an_update()
    {
        var (screen, _) = Build(running: "unknown");

        await screen.CheckCommand.ExecuteAsync(null);

        Assert.False(screen.CanDownload);
        Assert.Contains("publish.ps1", screen.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_reported_rather_than_thrown()
    {
        // What a captive hotspot portal returns instead of an API response.
        var (screen, _) = Build(reply: "<html>sign in to continue</html>");

        await screen.CheckCommand.ExecuteAsync(null);

        Assert.Contains("Could not read", screen.Headline, StringComparison.Ordinal);
        Assert.False(screen.CanDownload);
    }

    [Fact]
    public async Task A_release_with_no_file_for_this_machine_is_not_offered()
    {
        var (screen, _) = Build(reply: """
            { "tag_name": "v.0.10.0", "assets": [] }
            """);

        await screen.CheckCommand.ExecuteAsync(null);

        Assert.False(screen.CanDownload);
        Assert.Contains("no file for this machine", screen.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checking_twice_does_not_stack_up()
    {
        var (screen, asked) = Build();

        await screen.CheckCommand.ExecuteAsync(null);
        await screen.CheckCommand.ExecuteAsync(null);

        Assert.Equal(2, asked());
        Assert.False(screen.IsBusy);
        Assert.True(screen.IsIdle);
    }
}
