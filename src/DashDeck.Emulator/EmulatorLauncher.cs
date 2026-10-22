using System.Diagnostics;
using DashDeck.Core.Emulation;

namespace DashDeck.Emulator;

/// <summary>An emulator running now, from its discovery file.</summary>
public sealed record RunningEmulator(int Pid, string Path, EmulatorDiscovery Discovery)
{
    /// <summary>The AVD's own name (<c>avd.id</c>), or null.</summary>
    public string? AvdId => Discovery.Keys.GetValueOrDefault("avd.id");
}

/// <summary>What starting an emulator came to.</summary>
/// <param name="Launcher">The <c>emulator</c> process, which stays running while the emulator does.</param>
/// <param name="Discovery">Its discovery file, with the gRPC port and token — null when it never wrote one.</param>
/// <param name="Pid">The virtual machine's own process, from the discovery file's name.</param>
/// <param name="Failure">Why it did not start, or null.</param>
public sealed record LaunchResult(Process? Launcher, EmulatorDiscovery? Discovery, int? Pid, string? Failure);

/// <summary>
/// Starting, finding and stopping emulators (ADR-0075) — everything the AndroidProbe learnt on the
/// Surface, kept: the discovery file is named after the virtual machine's process, not
/// <c>emulator.exe</c>; an emulator left running holds its AVD and the port; and shutting down over
/// gRPC saves the quick-boot snapshot, where killing the process does not.
/// </summary>
public static class EmulatorLauncher
{
    /// <summary>Emulators running now: discovery files whose process is alive and is an emulator.</summary>
    public static IReadOnlyList<RunningEmulator> Running()
    {
        var running = new List<RunningEmulator>();

        foreach (var folder in Folders().Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "pid_*.ini"))
            {
                if (EmulatorDiscovery.PidOf(path) is { } pid && IsEmulator(pid))
                {
                    try
                    {
                        running.Add(new RunningEmulator(pid, path, EmulatorDiscovery.Parse(File.ReadAllText(path))));
                    }
                    catch (IOException)
                    {
                        // Being written or removed as we look.
                    }
                }
            }
        }

        return running;
    }

    /// <summary>
    /// Start an AVD hidden with gRPC and a token, and wait for it to say where its gRPC is.
    /// <paramref name="memoryMb"/> and <paramref name="cores"/>, when given, override the AVD's own.
    /// </summary>
    public static async Task<LaunchResult> StartAsync(
        string sdk, string avd, int port, string? skin, Action<string> say, CancellationToken ct, int? memoryMb = null, int? cores = null)
    {
        ArgumentNullException.ThrowIfNull(say);

        var info = new ProcessStartInfo(AndroidSdk.EmulatorPath(sdk))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in EmulatorCommand.Arguments(avd, port, skin: skin, memoryMb: memoryMb, cores: cores))
        {
            info.ArgumentList.Add(argument);
        }

        var launched = DateTime.UtcNow.AddSeconds(-2);
        var output = new List<string>();
        Process process;

        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LaunchResult(null, null, null, $"the emulator would not start: {e.Message}");
        }

        process.OutputDataReceived += (_, e) => Keep(output, e.Data);
        process.ErrorDataReceived += (_, e) => Keep(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            ct.ThrowIfCancellationRequested();

            string? advertised;
            lock (output)
            {
                advertised = EmulatorDiscovery.AdvertisedPath(output);
            }

            var path = advertised is not null && File.Exists(advertised)
                ? advertised
                : EmulatorDiscovery.Newest(
                    Folders().Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, "pid_*.ini")).Select(f => (f, File.GetLastWriteTimeUtc(f))),
                    launched);

            if (path is not null)
            {
                var discovery = EmulatorDiscovery.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                if (discovery.GrpcPort is not null)
                {
                    say($"gRPC on port {discovery.GrpcPort}");
                    return new LaunchResult(process, discovery, EmulatorDiscovery.PidOf(path), null);
                }
            }

            if (process.HasExited)
            {
                string reason;
                lock (output)
                {
                    reason = output.LastOrDefault(l => l.Contains("FATAL", StringComparison.Ordinal) || l.Contains("ERROR", StringComparison.Ordinal))?.Trim()
                        ?? $"it exited with code {process.ExitCode}";
                }

                return new LaunchResult(null, null, null, $"the emulator stopped before it was ready: {reason}");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        process.Kill(entireProcessTree: true);
        return new LaunchResult(null, null, null, "the emulator never said where its gRPC is (90 s)");
    }

    /// <summary>
    /// Stop an emulator: through gRPC first, so its snapshot is saved, and by force only if it is
    /// still there a minute later. Never throws.
    /// </summary>
    public static async Task StopAsync(int? port, string? token, int? pid, Process? launcher)
    {
        var asked = false;

        if (port is { } p)
        {
            using var client = new EmulatorClient(p, token);
            asked = await client.ShutdownAsync().ConfigureAwait(false);
        }

        bool Alive() => (pid is { } vm && IsEmulator(vm)) || launcher is { HasExited: false };

        var watch = Stopwatch.StartNew();
        while (asked && Alive() && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(500).ConfigureAwait(false);
        }

        if (!Alive())
        {
            return;
        }

        try
        {
            if (pid is { } vm)
            {
                using var process = Process.GetProcessById(vm);
                process.Kill(entireProcessTree: true);
            }

            if (launcher is { HasExited: false })
            {
                launcher.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static IReadOnlyList<string> Folders() =>
        EmulatorDiscovery.Folders(Environment.GetEnvironmentVariable, Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static bool IsEmulator(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (process.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.StartsWith("emulator", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Keep(List<string> lines, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (lines)
        {
            if (lines.Count < 2000)
            {
                lines.Add(line);
            }
        }
    }
}
