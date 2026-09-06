using System.IO;
using DashDeck.Host.Stage;

namespace DashDeck.Host.Tests;

/// <summary>
/// The user-added stage apps (ADR-0024): the persisted list and the spec it turns into. The
/// hosting itself is the generic <c>AppStageOccupant</c> that ADR-0020/0021 already proved and a
/// screenshot cannot see, so what is worth testing here is the seam this change actually adds —
/// that a UI entry round-trips to disk and becomes a launchable spec.
/// </summary>
public sealed class UserAppTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"dashdeck-apps-{Guid.NewGuid():N}.json");

    [Fact]
    public void An_added_app_survives_a_reload()
    {
        var path = TempPath();

        try
        {
            var store = new UserAppStore(path);
            store.Add(new UserAppEntry { Name = "CHROME", Path = @"C:\chrome.exe", Arguments = "--kiosk" });

            // A fresh store at the same path is what a restart is.
            var reloaded = new UserAppStore(path);

            var app = Assert.Single(reloaded.Apps);
            Assert.Equal("CHROME", app.Name);
            Assert.Equal(@"C:\chrome.exe", app.Path);
            Assert.Equal("--kiosk", app.Arguments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_keep_playing_flag_survives_a_reload()
    {
        var path = TempPath();

        try
        {
            var store = new UserAppStore(path);
            store.Add(new UserAppEntry { Name = "CHROME", Path = @"C:\chrome.exe", KeepPlaying = true });

            Assert.True(Assert.Single(new UserAppStore(path).Apps).KeepPlaying);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Removing_an_app_persists_too()
    {
        var path = TempPath();

        try
        {
            var store = new UserAppStore(path);
            var entry = new UserAppEntry { Name = "CHROME", Path = @"C:\chrome.exe" };
            store.Add(entry);
            store.Remove(entry);

            Assert.Empty(new UserAppStore(path).Apps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Editing_the_list_raises_changed()
    {
        var path = TempPath();

        try
        {
            var store = new UserAppStore(path);
            var fired = 0;
            store.Changed += (_, _) => fired++;

            store.Add(new UserAppEntry { Name = "A", Path = @"C:\a.exe" });
            store.Remove(store.Apps[0]);

            // Twice: the shell rebuilds its stage options on each, which is what makes an added
            // app appear without a restart.
            Assert.Equal(2, fired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromUser_upper_cases_the_name_and_carries_the_arguments()
    {
        var entry = new UserAppEntry { Name = "  chrome  ", Path = @"C:\x\chrome.exe", Arguments = "--kiosk" };

        var spec = AppLaunchSpec.FromUser(entry);

        Assert.Equal("CHROME", spec.Name);
        Assert.Equal("--kiosk", spec.Arguments);
        Assert.Equal("chrome.exe", spec.Detail);   // detail is the file name
    }

    [Fact]
    public void FromUser_resolves_a_real_file_and_reports_it_installed()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"dashdeck-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "not really an exe");

        try
        {
            var spec = AppLaunchSpec.FromUser(new UserAppEntry { Name = "FAKE", Path = exe });

            Assert.True(spec.IsInstalled);
            Assert.Equal(exe, spec.Resolve());
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void A_missing_path_reads_as_not_installed_rather_than_throwing()
    {
        var spec = AppLaunchSpec.FromUser(new UserAppEntry { Name = "GONE", Path = @"C:\nope\missing.exe" });

        Assert.False(spec.IsInstalled);
        Assert.Null(spec.Resolve());
    }

    [Theory]
    [InlineData(StageKind.Screen, "SCREENS")]
    [InlineData(StageKind.Web, "WEB")]
    [InlineData(StageKind.App, "APPS")]
    public void The_group_label_follows_the_kind(StageKind kind, string label) =>
        Assert.Equal(label, new StageOption("X", "detail", null, kind).GroupLabel);
}
