using System.Diagnostics;
using DashDeck.Core.Phone;

namespace DashDeck.Phone;

/// <summary>
/// The owner's scrcpy, found where it was installed (ADR-0076): its server, which the dash pushes to
/// the phone, its version, which the server must be started with, and its own <c>adb</c>.
/// </summary>
/// <remarks>
/// DashDeck ships no part of scrcpy — the server is the owner's own install, used as scrcpy's own
/// client uses it. Found from, in order: the settings file's <c>androidScrcpy</c> folder,
/// <c>SCRCPY_SERVER_PATH</c>, the folders on <c>PATH</c>, winget's packages and links, scoop,
/// Chocolatey, and a folder unzipped into Downloads, the Desktop, the home folder or <c>C:\</c>.
/// </remarks>
/// <param name="Folder">Where scrcpy is.</param>
/// <param name="ServerPath">Its server, <c>scrcpy-server</c>.</param>
/// <param name="AdbPath">The <c>adb.exe</c> beside it, or null.</param>
public sealed record ScrcpyInstall(string Folder, string ServerPath, string? AdbPath, ScrcpyVersion Version)
{
    private const string ServerFile = "scrcpy-server";

    /// <summary>The first install found, or null when there is none.</summary>
    public static ScrcpyInstall? Find(string? configured)
    {
        foreach (var folder in Folders(configured).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (At(folder) is { } install)
            {
                return install;
            }
        }

        return null;
    }

    /// <summary>scrcpy in this folder, or null when its server is not there or its version cannot be told.</summary>
    public static ScrcpyInstall? At(string folder)
    {
        try
        {
            var server = Path.Combine(folder, ServerFile);
            if (!File.Exists(server))
            {
                return null;
            }

            var exe = Path.Combine(folder, OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy");
            var version = (File.Exists(exe) ? ScrcpyVersion.Parse(Ask(exe)) : null) ?? ScrcpyVersion.Parse(Path.GetFileName(folder));
            if (version is null)
            {
                return null;
            }

            var adb = Path.Combine(folder, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            return new ScrcpyInstall(folder, server, File.Exists(adb) ? adb : null, version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Folders(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        if (Environment.GetEnvironmentVariable("SCRCPY_SERVER_PATH") is { Length: > 0 } serverPath && Path.GetDirectoryName(serverPath) is { } serverFolder)
        {
            yield return serverFolder;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return folder.Trim('"');
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // winget puts a link in Links and the files in a package folder of their own.
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "scrcpy.exe");
        string? linked = null;
        try
        {
            linked = File.Exists(link) && File.ResolveLinkTarget(link, returnFinalTarget: true) is { } target ? Path.GetDirectoryName(target.FullName) : null;
        }
        catch (IOException)
        {
        }

        if (linked is not null)
        {
            yield return linked;
        }

        foreach (var folder in Under(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "Genymobile.scrcpy*").SelectMany(p => Under(p, "*").Prepend(p)))
        {
            yield return folder;
        }

        yield return Path.Combine(home, "scoop", "apps", "scrcpy", "current");

        foreach (var folder in Under(Path.Combine(programData, "chocolatey", "lib", "scrcpy", "tools"), "*").Prepend(Path.Combine(programData, "chocolatey", "lib", "scrcpy", "tools")))
        {
            yield return folder;
        }

        foreach (var parent in new[] { Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), home, Path.GetPathRoot(home) ?? "C:\\" })
        {
            // Newest version first, when several were unzipped side by side.
            foreach (var folder in Under(parent, "scrcpy*").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
            {
                yield return folder;
                foreach (var inner in Under(folder, "scrcpy*"))
                {
                    yield return inner;
                }
            }
        }
    }

    private static IEnumerable<string> Under(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder, pattern) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>What <c>scrcpy --version</c> says, at most five seconds.</summary>
    private static string? Ask(string exe)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return output.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
