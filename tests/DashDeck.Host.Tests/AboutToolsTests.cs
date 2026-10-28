using System.IO;
using DashDeck.Abstractions;
using DashDeck.Host.Settings;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Tests;

/// <summary>
/// Settings ▸ ABOUT ▸ TOOLS (ADR-0079): a button for each tool beside the dash, two taps, and the
/// dash closing so the tool gets the adapter. And the touch keyboard's line.
/// </summary>
public sealed class AboutToolsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dashdeck-tools-").FullName;
    private readonly TestClock _clock = new();
    private readonly List<string> _opened = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Every_tool_is_listed_and_one_not_there_says_so()
    {
        Ship("IdHunter", "IdHunter.exe");

        var about = About();

        Assert.Equal(["ID hunter", "ID matcher", "Serial tap"], about.Tools.Select(t => t.Name));
        Assert.True(about.Tools[0].IsThere);
        Assert.Equal("", about.Tools[0].State);
        Assert.False(about.Tools[1].IsThere);
        Assert.Equal("not in this install", about.Tools[1].State);
    }

    [Fact]
    public void The_first_tap_arms_and_the_second_opens()
    {
        var hunter = Ship("IdHunter", "IdHunter.exe");
        var about = About();
        var row = about.Tools[0];

        about.OpenToolCommand.Execute(row);

        Assert.Empty(_opened);
        Assert.True(row.IsArmed);
        Assert.Equal("TAP AGAIN: DASHDECK CLOSES", row.Caption);

        _clock.Advance(TimeSpan.FromSeconds(1));
        about.OpenToolCommand.Execute(row);

        Assert.Equal([hunter], _opened);
        Assert.False(row.IsArmed);
    }

    [Fact]
    public void A_second_tap_too_late_only_arms_again()
    {
        Ship("IdHunter", "IdHunter.exe");
        var about = About();

        about.OpenToolCommand.Execute(about.Tools[0]);
        _clock.Advance(TimeSpan.FromSeconds(10));
        about.OpenToolCommand.Execute(about.Tools[0]);

        Assert.Empty(_opened);
        Assert.True(about.Tools[0].IsArmed);
    }

    [Fact]
    public void Arming_one_tool_disarms_another()
    {
        Ship("IdHunter", "IdHunter.exe");
        Ship("IdMatcher", "IdMatcher.exe");
        var about = About();

        about.OpenToolCommand.Execute(about.Tools[0]);
        about.OpenToolCommand.Execute(about.Tools[1]);
        about.OpenToolCommand.Execute(about.Tools[0]);

        Assert.Empty(_opened);
        Assert.True(about.Tools[0].IsArmed);
        Assert.False(about.Tools[1].IsArmed);
    }

    [Fact]
    public void A_missing_tool_opens_nothing_and_says_where_it_looked()
    {
        var about = About();

        about.OpenToolCommand.Execute(about.Tools[2]);
        about.OpenToolCommand.Execute(about.Tools[2]);

        Assert.Empty(_opened);
        Assert.Contains("Serial tap is not in this install", about.Problem);
    }

    [Fact]
    public void A_tool_that_would_not_start_says_why()
    {
        Ship("SerialTap", "SerialTap.exe");
        var about = new AboutViewModel(new DisplaySettings(), _clock, _folder, _ => "Could not open it: denied", () => Task.FromResult("x"));

        about.OpenToolCommand.Execute(about.Tools[2]);
        about.OpenToolCommand.Execute(about.Tools[2]);

        Assert.Equal("Could not open it: denied", about.Problem);
    }

    [Fact]
    public void The_keyboard_line_says_what_the_rule_sees()
    {
        var about = About("a keyboard is attached: Logitech K380");

        Assert.Contains(about.Facts, f => f.Label == "Touch keyboard" && f.Value == "a keyboard is attached: Logitech K380");
    }

    private AboutViewModel About(string keyboard = "no keyboard attached") =>
        new(new DisplaySettings(), _clock, _folder, path =>
        {
            _opened.Add(path);
            return null;
        },
        () => Task.FromResult(keyboard));

    private string Ship(string folder, string exe)
    {
        var path = Path.Combine(_folder, folder, exe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
