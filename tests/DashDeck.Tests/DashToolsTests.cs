using DashDeck.Core.Tools;

namespace DashDeck.Tests;

/// <summary>
/// The tools in a release, and the script that opens one once the dash has let go of the adapter
/// (ADR-0079).
/// </summary>
public sealed class DashToolsTests
{
    private const string Hunter = @"C:\Users\ABC\Documents\app\DashDeck\IdHunter\IdHunter.exe";

    [Fact]
    public void The_list_is_what_the_release_ships()
    {
        var publish = File.ReadAllText(Path.Combine(RepoRoot(), "publish.ps1"));

        Assert.Equal(["ID hunter", "ID matcher", "Serial tap"], DashTools.All.Select(t => t.Name));

        foreach (var tool in DashTools.All)
        {
            var folder = tool.RelativePath.Split('\\')[0];
            Assert.Contains($"Join-Path $Output '{folder}'", publish);
        }
    }

    [Fact]
    public void The_tool_starts_only_after_the_wait_for_the_dash()
    {
        var script = DashTools.LaunchScript(4242, Hunter);

        var wait = script.IndexOf("tasklist /FI \"PID eq 4242\"", StringComparison.Ordinal);
        var start = script.IndexOf("start \"\"", StringComparison.Ordinal);

        Assert.True(wait > 0);
        Assert.True(start > wait);
    }

    [Fact]
    public void Paths_are_quoted_and_the_tool_starts_in_its_own_folder()
    {
        var script = DashTools.LaunchScript(4242, Hunter);

        Assert.Contains($"start \"\" /D \"C:\\Users\\ABC\\Documents\\app\\DashDeck\\IdHunter\" \"{Hunter}\"", script);
    }

    [Fact]
    public void A_dash_that_never_exits_still_gets_its_tool_opened()
    {
        var script = DashTools.LaunchScript(4242, Hunter);

        Assert.Contains($"if %tries% GEQ {DashTools.WaitSeconds} goto gone", script);
    }

    [Fact]
    public void It_stands_outside_the_dash_folder_and_deletes_itself()
    {
        var script = DashTools.LaunchScript(4242, Hunter);

        Assert.StartsWith("@echo off\r\n", script);
        Assert.Contains("cd /d \"%TEMP%\"", script);
        Assert.Contains("del \"%~f0\"", script);
        Assert.DoesNotContain("\n", script.Replace("\r\n", string.Empty));
    }

    [Theory]
    [InlineData(0, Hunter)]
    [InlineData(4242, "")]
    [InlineData(4242, "IdHunter.exe")]
    public void A_missing_process_or_path_is_refused(int processId, string path) =>
        Assert.ThrowsAny<ArgumentException>(() => DashTools.LaunchScript(processId, path));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "publish.ps1")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("publish.ps1 not found above the tests");
    }
}
