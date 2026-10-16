using DashDeck.Core.Updates;

namespace DashDeck.Tests;

/// <summary>
/// Reading versions out of the three places that write them differently (ADR-0061).
/// </summary>
public sealed class ReleaseVersionTests
{
    [Theory]
    // The tag form this project uses, stray dot and all.
    [InlineData("v.0.9.0", 0, 9, 0)]
    // The asset form, which drops the dot.
    [InlineData("v0.9.0", 0, 9, 0)]
    // The older tags, before the dot appeared.
    [InlineData("v0.6.1", 0, 6, 1)]
    // An assembly version's fourth part is dropped, not compared.
    [InlineData("0.9.0.0", 0, 9, 0)]
    [InlineData("0.9.0.7", 0, 9, 0)]
    // Informational versions carry build metadata.
    [InlineData("0.9.0+1a2b3c", 0, 9, 0)]
    [InlineData("1.0.0-rc1", 1, 0, 0)]
    // Two parts means a zero patch.
    [InlineData("v1.2", 1, 2, 0)]
    // Double digits sort as numbers, which is the whole reason this is parsed rather than compared
    // as text: "0.10.0" is less than "0.9.0" alphabetically and newer in fact.
    [InlineData("v.0.10.0", 0, 10, 0)]
    [InlineData("  v.2.0.1  ", 2, 0, 1)]
    public void A_version_is_read_from_any_of_the_forms_this_project_writes(
        string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // What the About screen shows when an assembly has no version attribute at all.
    [InlineData("unknown")]
    [InlineData("v")]
    [InlineData("v.")]
    [InlineData("9")]
    [InlineData("0.9.0.beta")]
    [InlineData("1.2.3.4.5")]
    [InlineData("latest")]
    [InlineData("-1.0.0")]
    public void Anything_that_is_not_a_version_is_refused(string? text) =>
        Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Ten_is_newer_than_nine_which_is_the_comparison_text_gets_wrong()
    {
        Assert.True(ReleaseVersion.TryParse("v.0.9.0", out var nine));
        Assert.True(ReleaseVersion.TryParse("v.0.10.0", out var ten));

        Assert.True(nine.IsOlderThan(ten));
        Assert.False(ten.IsOlderThan(nine));
    }

    [Fact]
    public void The_same_version_is_not_older_than_itself()
    {
        var version = new ReleaseVersion(0, 9, 0);

        Assert.False(version.IsOlderThan(version));
    }

    [Fact]
    public void A_version_reads_back_in_the_plain_form()
    {
        Assert.Equal("0.9.0", new ReleaseVersion(0, 9, 0).ToString());
    }
}

/// <summary>
/// What a check concludes, which decides which buttons a person is offered (ADR-0061).
/// </summary>
public sealed class UpdateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static ReleaseAsset Zip(string name = "DashDeck-v0.10.0-win-x64.zip", long bytes = 350_000_000) =>
        new(name, $"https://example.invalid/{name}", bytes);

    [Fact]
    public void A_newer_release_with_a_file_is_offered()
    {
        var status = UpdateCheck.Evaluate("0.9.0", "v.0.10.0", [Zip()]);

        Assert.Equal(UpdateVerdict.Available, status.Verdict);
        Assert.Equal(new ReleaseVersion(0, 10, 0), status.Latest);
        Assert.NotNull(status.Asset);
        Assert.Contains("0.10.0 is available", UpdateCheck.Describe(status), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_version_is_up_to_date_rather_than_available()
    {
        var status = UpdateCheck.Evaluate("0.9.0", "v.0.9.0", [Zip("DashDeck-v0.9.0-win-x64.zip")]);

        Assert.Equal(UpdateVerdict.UpToDate, status.Verdict);
        Assert.Contains("nothing newer", UpdateCheck.Describe(status), StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_newer_than_the_latest_release_is_up_to_date_not_downgraded()
    {
        // Running a build from develop that is ahead of the last tag. Offering to "update" to an
        // older release would replace a working dash with a worse one.
        var status = UpdateCheck.Evaluate("0.11.0", "v.0.10.0", [Zip()]);

        Assert.Equal(UpdateVerdict.UpToDate, status.Verdict);
    }

    [Fact]
    public void A_build_that_cannot_name_itself_is_never_told_it_is_out_of_date()
    {
        // This is the one that decides whether a tablet replaces its own folder, so an unknown
        // running version is its own verdict rather than being treated as very old.
        var status = UpdateCheck.Evaluate("unknown", "v.0.10.0", [Zip()]);

        Assert.Equal(UpdateVerdict.UnknownRunningVersion, status.Verdict);
        Assert.Null(status.Asset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void A_release_whose_tag_is_not_a_version_is_unreadable(string? tag)
    {
        var status = UpdateCheck.Evaluate("0.9.0", tag, [Zip()]);

        Assert.Equal(UpdateVerdict.Unreadable, status.Verdict);
        Assert.Equal(new ReleaseVersion(0, 9, 0), status.Running);
    }

    [Fact]
    public void A_newer_release_with_nothing_to_download_says_so_rather_than_offering_a_button()
    {
        var status = UpdateCheck.Evaluate("0.9.0", "v.0.10.0", [new ReleaseAsset("notes.txt", "https://example.invalid/n", 12)]);

        Assert.Equal(UpdateVerdict.NoDownload, status.Verdict);
        Assert.Null(status.Asset);
        Assert.Contains("-win-x64.zip", status.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_asset_with_no_bytes_is_not_a_download()
    {
        // A release can carry an asset that failed to upload; its size comes back zero.
        var status = UpdateCheck.Evaluate("0.9.0", "v.0.10.0", [Zip(bytes: 0)]);

        Assert.Equal(UpdateVerdict.NoDownload, status.Verdict);
    }

    [Fact]
    public void A_second_architecture_beside_it_is_not_ambiguous()
    {
        var status = UpdateCheck.Evaluate(
            "0.9.0",
            "v.0.10.0",
            [Zip(), new ReleaseAsset("DashDeck-v0.10.0-win-arm64.zip", "https://example.invalid/a", 340_000_000)]);

        Assert.Equal(UpdateVerdict.Available, status.Verdict);
        Assert.Equal("DashDeck-v0.10.0-win-x64.zip", status.Asset!.Value.Name);
    }

    [Fact]
    public void Two_usable_files_are_resolved_by_the_one_naming_this_version()
    {
        var status = UpdateCheck.Evaluate(
            "0.9.0",
            "v.0.10.0",
            [Zip("DashDeck-v0.9.0-win-x64.zip"), Zip("DashDeck-v0.10.0-win-x64.zip")]);

        Assert.Equal("DashDeck-v0.10.0-win-x64.zip", status.Asset!.Value.Name);
    }

    [Fact]
    public void Two_usable_files_that_both_look_right_are_refused_rather_than_guessed()
    {
        // 334 MB over a phone hotspot is not worth a coin flip.
        var status = UpdateCheck.Evaluate(
            "0.9.0",
            "v.0.10.0",
            [Zip("DashDeck-v0.10.0-win-x64.zip"), Zip("DashDeck-v0.10.0-rebuilt-win-x64.zip")]);

        Assert.Equal(UpdateVerdict.NoDownload, status.Verdict);
    }

    [Fact]
    public void Nothing_has_been_checked_until_something_is_asked()
    {
        // The resting state of the screen, and the reason it exists: nothing checks on its own.
        Assert.Equal("Not checked", UpdateCheck.Describe(new UpdateStatus(UpdateVerdict.NotChecked)));
    }

    [Theory]
    [InlineData(0, "published today")]
    [InlineData(1, "published yesterday")]
    [InlineData(9, "published 9 days ago")]
    public void Age_is_in_days_because_the_question_is_whether_it_is_fresh(int daysAgo, string expected) =>
        Assert.Equal(expected, UpdateCheck.Age(Now.AddDays(-daysAgo), Now));

    [Fact]
    public void A_release_with_no_date_says_nothing_about_its_age() =>
        Assert.Equal("", UpdateCheck.Age(null, Now));

    [Fact]
    public void A_publish_date_in_the_future_reads_as_just_now_rather_than_negative() =>
        Assert.Equal("published just now", UpdateCheck.Age(Now.AddHours(3), Now));

    [Fact]
    public void Sizes_are_megabytes() => Assert.Equal("334 MB", UpdateCheck.Size(350_224_384));
}

/// <summary>
/// The questions asked before a folder is replaced. Each wrong answer ends with a dash that will
/// not start, so each one is pinned (ADR-0061).
/// </summary>
public sealed class InstallGuardTests
{
    private const long Archive = 350_000_000;

    private static InstallRefusal Check(
        string folder = @"C:\Users\ABC\Documents\app\dist\DashDeck",
        bool hasExecutable = true,
        long archiveBytes = Archive,
        long downloadedBytes = Archive,
        bool archiveHoldsDashDeck = true,
        long freeBytes = Archive * 4) =>
        InstallGuards.Check(folder, hasExecutable, archiveBytes, downloadedBytes, archiveHoldsDashDeck, freeBytes);

    [Fact]
    public void A_deploy_with_room_and_a_complete_archive_proceeds() =>
        Assert.Equal(InstallRefusal.None, Check());

    [Theory]
    [InlineData(@"C:\code\DashDeck\src\DashDeck.Host\bin\Release\net10.0-windows10.0.19041.0")]
    [InlineData(@"C:\code\DashDeck\src\DashDeck.Host\obj\Debug")]
    [InlineData(@"C:/code/DashDeck/src/DashDeck.Host/bin/Debug/net10.0-windows")]
    [InlineData(@"C:\code\DashDeck\src\DashDeck.Host\bin")]
    public void A_build_folder_is_never_swapped(string folder) =>
        Assert.Equal(InstallRefusal.DevelopmentBuild, Check(folder));

    [Fact]
    public void A_folder_with_no_executable_is_not_a_deploy() =>
        Assert.Equal(InstallRefusal.NotADeploy, Check(hasExecutable: false));

    [Fact]
    public void A_download_that_stopped_short_is_not_installed() =>
        Assert.Equal(InstallRefusal.ArchiveIncomplete, Check(downloadedBytes: Archive / 2));

    [Fact]
    public void A_download_of_nothing_at_all_is_not_installed() =>
        Assert.Equal(InstallRefusal.ArchiveIncomplete, Check(downloadedBytes: 0));

    [Fact]
    public void A_release_that_does_not_say_its_size_is_trusted_if_something_arrived()
    {
        // GitHub always reports a size; a mirror or a hand-attached file might not. Refusing in
        // that case would block an update over a missing number rather than a missing file.
        Assert.Equal(InstallRefusal.None, Check(archiveBytes: 0, downloadedBytes: Archive));
    }

    [Fact]
    public void An_archive_that_is_not_a_dashdeck_is_refused() =>
        Assert.Equal(InstallRefusal.ArchiveNotDashDeck, Check(archiveHoldsDashDeck: false));

    [Fact]
    public void Room_is_needed_for_three_copies_not_one()
    {
        // The archive, the unpacked copy, and the previous folder that is kept. Two copies' worth
        // of space is not enough, and finding that out halfway through a move is the bad case.
        Assert.Equal(InstallRefusal.NotEnoughRoom, Check(freeBytes: Archive * 2));
        Assert.Equal(InstallRefusal.None, Check(freeBytes: Archive * 3));
    }

    [Fact]
    public void Every_refusal_says_something_a_person_can_act_on()
    {
        foreach (var refusal in Enum.GetValues<InstallRefusal>().Where(r => r != InstallRefusal.None))
        {
            var text = InstallGuards.Explain(refusal, Archive);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.EndsWith(".", text.Trim(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_in_the_way_explains_nothing() =>
        Assert.Equal("", InstallGuards.Explain(InstallRefusal.None));
}

/// <summary>
/// The batch file that does the swap. Its line order is the difference between a recoverable
/// failure and a tablet with no dash on it in a parking lot (ADR-0061).
/// </summary>
public sealed class SwapScriptTests
{
    private static readonly SwapRequest Request = new(
        ProcessId: 4242,
        CurrentFolder: @"C:\Users\ABC\Documents\app\dist\DashDeck",
        StagedFolder: @"C:\Users\ABC\AppData\Local\DashDeck\updates\staged",
        PreviousFolder: @"C:\Users\ABC\Documents\app\dist\DashDeck.previous",
        ExecutablePath: @"C:\Users\ABC\Documents\app\dist\DashDeck\DashDeck.Host.exe",
        LogPath: @"C:\Users\ABC\AppData\Local\DashDeck\updates\swap.log");

    private static string Script => SwapScript.Build(Request);

    /// <summary>
    /// The lines cmd will actually run, without the comments. Written because the first test here
    /// matched the word "move" inside a <c>rem</c> and concluded the script moved a folder before
    /// waiting for the process — the script was right and the test was reading prose.
    /// </summary>
    private static List<string> Commands(string script) =>
        [.. script.Split('\n').Select(l => l.Trim()).Where(l =>
            l.Length > 0
            && !l.StartsWith("rem ", StringComparison.Ordinal)
            && !l.StartsWith("@echo", StringComparison.Ordinal))];

    private static int IndexOfCommand(string script, string startsWith) =>
        Commands(script).FindIndex(l => l.StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void It_waits_for_the_dash_to_exit_before_touching_anything()
    {
        var script = Script;

        var wait = IndexOfCommand(script, "tasklist");
        var firstMove = IndexOfCommand(script, "move ");

        Assert.True(wait >= 0, "the script must wait for the process");
        Assert.True(firstMove > wait, "the wait must come before the first move");
        Assert.Contains("PID eq 4242", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dash_that_will_not_exit_leaves_everything_alone()
    {
        var script = Script;

        Assert.Contains("did not exit", script, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", script, StringComparison.Ordinal);

        // The bail-out has to be before the moves, or "nothing was changed" is a lie.
        Assert.True(IndexOfCommand(script, ":stillrunning") < IndexOfCommand(script, "move "));
    }

    [Fact]
    public void The_current_folder_is_moved_aside_and_never_deleted()
    {
        var script = Script;

        Assert.Contains($"move {SwapScript.Quote(Request.CurrentFolder)} {SwapScript.Quote(Request.PreviousFolder)}", script, StringComparison.Ordinal);

        // The only recursive delete is of the generation before last. Nothing deletes the folder
        // being replaced, which is what makes a failed swap recoverable.
        var deletes = Commands(script).Where(l => l.Contains("rd /s /q", StringComparison.Ordinal)).ToList();

        Assert.Single(deletes);
        Assert.Contains(SwapScript.Quote(Request.PreviousFolder), deletes[0], StringComparison.Ordinal);
        Assert.DoesNotContain(SwapScript.Quote(Request.CurrentFolder), deletes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_folder_goes_back_if_the_new_one_cannot_be_moved_in()
    {
        var script = Script;

        var rollback = $"move {SwapScript.Quote(Request.PreviousFolder)} {SwapScript.Quote(Request.CurrentFolder)}";

        Assert.Contains(rollback, script, StringComparison.Ordinal);

        // And it must come after the attempt it recovers from.
        Assert.True(
            script.IndexOf(rollback, StringComparison.Ordinal)
                > script.IndexOf($"move {SwapScript.Quote(Request.StagedFolder)}", StringComparison.Ordinal));
    }

    [Fact]
    public void It_starts_the_dash_again_and_removes_itself()
    {
        var script = Script;

        Assert.Contains($"start \"\" {SwapScript.Quote(Request.ExecutablePath)}", script, StringComparison.Ordinal);
        Assert.Contains("del \"%~f0\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_path_is_quoted_because_Documents_has_a_space_in_it()
    {
        var script = Script;

        // The tablet's deploy really does live under a path with a space, and an unquoted move in
        // a batch file goes somewhere nobody intended.
        var moves = Commands(script).Where(l => l.StartsWith("move ", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, moves.Count);

        foreach (var line in moves)
        {
            // Two quoted paths, then the quoted log it appends to: every path on the line.
            Assert.DoesNotContain(@"app\dist\DashDeck ", line, StringComparison.Ordinal);
            Assert.Equal(0, line.Count(c => c == '"') % 2);
            Assert.True(line.Count(c => c == '"') >= 4, line);
        }
    }

    [Fact]
    public void A_trailing_separator_does_not_become_an_escaped_quote()
    {
        // "C:\folder\" in a batch file escapes the quote and swallows the rest of the line.
        Assert.Equal("\"C:\\folder\"", SwapScript.Quote(@"C:\folder\"));
        Assert.Equal("\"C:\\folder\"", SwapScript.Quote(@"C:\folder"));
    }

    [Fact]
    public void It_is_written_with_windows_line_endings()
    {
        // A batch file with bare newlines runs, until a label or a goto does not.
        Assert.Contains("\r\n", Script, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", Script.Replace("\r\n", "\n").Replace("\n\n", "~"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_script_without_a_process_to_wait_for_is_refused(int pid) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SwapScript.Build(Request with { ProcessId = pid }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_is_refused_rather_than_written_into_a_move(string blank)
    {
        // A blank path in a batch move is how a script deletes the wrong thing.
        Assert.Throws<ArgumentException>(() => SwapScript.Build(Request with { CurrentFolder = blank }));
        Assert.Throws<ArgumentException>(() => SwapScript.Build(Request with { StagedFolder = blank }));
        Assert.Throws<ArgumentException>(() => SwapScript.Build(Request with { PreviousFolder = blank }));
        Assert.Throws<ArgumentException>(() => SwapScript.Build(Request with { ExecutablePath = blank }));
        Assert.Throws<ArgumentException>(() => SwapScript.Build(Request with { LogPath = blank }));
    }
}
