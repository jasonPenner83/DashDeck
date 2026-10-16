using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using DashDeck.Core.Updates;

namespace DashDeck.Host.Updates;

/// <summary>
/// Finding, fetching and installing a newer DashDeck — only ever because someone asked.
/// </summary>
/// <remarks>
/// <b>Nothing in here runs at launch</b> (ADR-0061). No background check, no badge, no toast: a
/// dash that interrupts a drive to mention a release is worse than one that is a version behind,
/// and the person pressing the button is the only signal that this is a good moment.
/// <para>
/// The decisions — is it newer, which file, may it be installed, what does the swap script say —
/// are all in <c>DashDeck.Core.Updates</c> where they are tested. What is left here is the part
/// that cannot be: an HTTP request, a long download, a zip, and handing a batch file to cmd.
/// </para>
/// <para>
/// C1 is intact. Nothing is installed: the dash replaces its own folder, the helper is a batch
/// file in the temp folder that deletes itself, and there is no service, no scheduled task and no
/// registry key anywhere in it.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    /// <summary>Where releases are published.</summary>
    public const string ReleasesUrl = "https://api.github.com/repos/jasonPenner83/DashDeck/releases/latest";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly string _runningVersion;
    private readonly string _runningFolder;

    /// <summary>The ordinary one: asks GitHub, looks at its own folder.</summary>
    public UpdateService()
        : this(RunningVersion(), AppContext.BaseDirectory, FetchAsync)
    {
    }

    /// <summary>For tests and for a dry run against a different folder or a canned response.</summary>
    public UpdateService(
        string runningVersion,
        string runningFolder,
        Func<string, CancellationToken, Task<string>> fetch)
    {
        _runningVersion = runningVersion;
        _runningFolder = runningFolder;
        _fetch = fetch;
    }

    /// <summary>Where downloads and the unpacked copy are kept, outside the folder being replaced.</summary>
    public static string UpdatesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashDeck",
        "updates");

    /// <summary>What this build says it is, as the About screen shows it.</summary>
    public string Version => _runningVersion;

    /// <summary>Ask what the newest release is. Returns a verdict, never throws for a dead link.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken cancel = default)
    {
        string json;

        try
        {
            json = await _fetch(ReleasesUrl, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline in a parking lot is the normal case, not an error worth a stack trace.
            return new UpdateStatus(UpdateVerdict.Unreadable, Problem: ex.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var notes = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            DateTimeOffset? published = root.TryGetProperty("published_at", out var p)
                && p.ValueKind is JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), out var at)
                    ? at
                    : null;

            var assets = new List<ReleaseAsset>();

            if (root.TryGetProperty("assets", out var list) && list.ValueKind is JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    {
                        assets.Add(new ReleaseAsset(name, url, size));
                    }
                }
            }

            return UpdateCheck.Evaluate(_runningVersion, tag, assets, published, notes);
        }
        catch (JsonException ex)
        {
            return new UpdateStatus(UpdateVerdict.Unreadable, Problem: $"the reply was not what we expected: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch the archive, reporting progress 0–1. Writes to a <c>.part</c> file and renames on
    /// completion, so an interrupted download is never mistaken for a finished one.
    /// </summary>
    public async Task<string> DownloadAsync(
        ReleaseAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        Directory.CreateDirectory(UpdatesFolder);

        var target = Path.Combine(UpdatesFolder, Path.GetFileName(asset.Name));
        var part = target + ".part";

        // No timeout on the download itself, only the caller's cancellation. HttpClient.Timeout
        // covers reading the body as well as the headers, so the 20 s that suits a version check
        // would abort every real download — 334 MB over a phone hotspot is minutes, not seconds.
        using var client = Client(forDownload: true);
        using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Bytes;

        await using (var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        await using (var destination = File.Create(part))
        {
            var buffer = new byte[256 * 1024];
            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                written += read;

                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)written / total, 0, 1));
                }
            }
        }

        File.Move(part, target, overwrite: true);
        progress?.Report(1);

        return target;
    }

    /// <summary>
    /// Decide whether the downloaded archive may replace this folder. Gathers the facts; the
    /// judgement is <see cref="InstallGuards"/>, which is tested.
    /// </summary>
    public InstallRefusal MayInstall(string archivePath, ReleaseAsset asset)
    {
        var downloaded = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;

        return InstallGuards.Check(
            _runningFolder,
            File.Exists(Path.Combine(_runningFolder, "DashDeck.Host.exe")),
            asset.Bytes,
            downloaded,
            HoldsDashDeck(archivePath),
            FreeBytes(_runningFolder));
    }

    /// <summary>Unpack the archive to a staging folder beside it, replacing any earlier attempt.</summary>
    public string Stage(string archivePath, IProgress<double>? progress = null)
    {
        var staged = Path.Combine(UpdatesFolder, "staged");

        if (Directory.Exists(staged))
        {
            Directory.Delete(staged, recursive: true);
        }

        Directory.CreateDirectory(staged);

        using var archive = ZipFile.OpenRead(archivePath);
        var done = 0;

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staged, entry.FullName));

            // A zip can name an entry ..\..\somewhere. Refusing beats trusting the archive.
            if (!destination.StartsWith(staged, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"the archive tries to write outside its folder: {entry.FullName}");
            }

            if (entry.Length == 0 && entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            progress?.Report(Math.Clamp((double)++done / archive.Entries.Count, 0, 1));
        }

        // The deploy may be at the root of the zip or one folder down, depending on how it was
        // made. Whichever it is, the folder that gets moved into place is the one with the exe.
        return FolderHoldingExecutable(staged) ?? staged;
    }

    /// <summary>
    /// Hand the swap to cmd and stop. The caller shuts the dash down immediately afterwards —
    /// the script waits for this process to exit before it touches anything.
    /// </summary>
    public string InstallAndRestart(string stagedFolder)
    {
        var script = SwapScript.Build(new SwapRequest(
            Environment.ProcessId,
            _runningFolder.TrimEnd(Path.DirectorySeparatorChar),
            stagedFolder.TrimEnd(Path.DirectorySeparatorChar),
            _runningFolder.TrimEnd(Path.DirectorySeparatorChar) + ".previous",
            Path.Combine(_runningFolder, "DashDeck.Host.exe"),
            Path.Combine(UpdatesFolder, "swap.log")));

        var path = Path.Combine(Path.GetTempPath(), $"dashdeck-swap-{Environment.ProcessId}.cmd");
        File.WriteAllText(path, script);

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        return path;
    }

    /// <summary>Whether the archive is a DashDeck deploy at all.</summary>
    private static bool HoldsDashDeck(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            return archive.Entries.Any(e =>
                e.Name.Equals("DashDeck.Host.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Not a zip, truncated, or unreadable — all of which mean "do not install this".
            return false;
        }
    }

    private static string? FolderHoldingExecutable(string root)
    {
        if (File.Exists(Path.Combine(root, "DashDeck.Host.exe")))
        {
            return root;
        }

        return Directory
            .EnumerateDirectories(root)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "DashDeck.Host.exe")));
    }

    private static long FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // An unknown volume should not block an update; the moves will say if it is full.
            return long.MaxValue;
        }
    }

    private static string RunningVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static async Task<string> FetchAsync(string url, CancellationToken cancel)
    {
        using var client = Client();

        return await client.GetStringAsync(url, cancel).ConfigureAwait(false);
    }

    private static HttpClient Client(bool forDownload = false)
    {
        var client = new HttpClient
        {
            Timeout = forDownload ? Timeout.InfiniteTimeSpan : CheckTimeout,
        };

        // GitHub refuses a request with no user agent, and says so in a way that reads like an
        // outage rather than a missing header.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DashDeck", RunningVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }
}
