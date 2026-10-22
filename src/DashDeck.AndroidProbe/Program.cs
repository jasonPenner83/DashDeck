using System.Diagnostics;
using System.Globalization;
using System.Text;
using Android.Emulation.Control;
using DashDeck.Core.Emulation;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Environment = System.Environment;

// AndroidProbe — can this tablet run Android as its own device on the stage? (ADR-0075)
//
// The plan is the Android Emulator running hidden, with DashDeck drawing its screen and sending it
// touch over the emulator's gRPC interface — the way Android Studio draws the emulator in its own
// panel. Whether that is any good on a Surface Pro 7 is not something to find out by building the
// stage occupant first. So this starts an AVD the way the dash would, and says:
//
//   the SDK, the emulator's version and whether virtualisation is usable
//   each AVD, and whether it is Automotive or regular Android
//   how the emulator says where its gRPC is (the discovery file), with secrets hidden
//   how long it takes to boot
//   frames a second at the stage's size, idle and while a finger drags, and the longest stutter
//   how long a touch takes to show
//   how much memory and processor the emulator takes
//   whether audio and a GPS position cross
//
// It writes a report to %LOCALAPPDATA%\DashDeck\androidprobe\. Nothing here touches the vehicle,
// installs anything, or changes a setting: the SDK and the AVDs are the person's own.

var started = Stopwatch.StartNew();
var report = new StringBuilder();
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

try
{
    return await RunAsync().ConfigureAwait(false);
}
finally
{
    Save();
}

async Task<int> RunAsync()
{
    if (Flag("--help") || Flag("-h"))
    {
        Usage();
        return 0;
    }

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var candidates = AndroidSdk.Candidates(Value("--sdk"), Environment.GetEnvironmentVariable, localAppData);
    var sdk = AndroidSdk.Find(candidates, File.Exists);

    Say("AndroidProbe — Android on the stage (ADR-0075)");
    Say($"  looked for the SDK in: {string.Join(", ", candidates)}");

    // What is running already. An emulator left running — by Android Studio, or by an earlier run
    // that was stopped hard — holds its AVD and usually the gRPC port, and a second copy of the same
    // AVD refuses to start. Found on the Surface: the probe was talking to the old one.
    var running = Running(profile);
    foreach (var (pid, path, other) in running)
    {
        Say($"  ALREADY RUNNING: {other.AvdName ?? "an AVD"} (process {pid}, gRPC port {other.GrpcPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}) — {path}");
    }

    if (Flag("--stop-running"))
    {
        foreach (var (pid, _, other) in running)
        {
            await ShutdownAsync(other.GrpcPort, other.GrpcToken, pid, null).ConfigureAwait(false);
        }

        running = Running(profile);
    }

    // An emulator already running — started by Android Studio, say — needs no SDK found here.
    if (Value("--attach") is { } attach)
    {
        var attachPort = int.Parse(attach, CultureInfo.InvariantCulture);
        var known = running.FirstOrDefault(r => r.Discovery.GrpcPort == attachPort);
        return await ProbeAsync(null, attachPort, Value("--token") ?? known.Discovery?.GrpcToken, known.Discovery is null ? null : known.Pid).ConfigureAwait(false);
    }

    if (sdk is null)
    {
        Say("  NO EMULATOR FOUND. Install Android Studio (or the command-line tools and the emulator), or pass --sdk <folder>.");
        return 1;
    }

    var emulator = AndroidSdk.EmulatorPath(sdk);
    Say($"  SDK: {sdk}");
    Say($"  emulator: {FirstLine(await CaptureAsync(emulator, "-version").ConfigureAwait(false))}");
    Say("  virtualisation (emulator -accel-check):");
    foreach (var line in (await CaptureAsync(emulator, "-accel-check").ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        Say("    " + line.TrimEnd());
    }

    var avdHome = AndroidSdk.AvdHome(Environment.GetEnvironmentVariable, profile);
    var avds = (await CaptureAsync(emulator, "-list-avds").ConfigureAwait(false))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(a => !a.StartsWith("INFO", StringComparison.Ordinal) && !a.Contains(' ', StringComparison.Ordinal))
        .ToList();

    Say($"  AVDs in {avdHome}: {(avds.Count == 0 ? "none — make one in Android Studio's Device Manager" : "")}");
    foreach (var name in avds)
    {
        Say("    " + (ReadAvd(avdHome, name)?.Describe() ?? $"{name} — config.ini not found"));
    }

    if (Value("--avd") is not { } avd)
    {
        Say("");
        Say("Pass --avd <name> to start one and measure it, or --attach <port> for one already running.");
        return 0;
    }

    if (!avds.Contains(avd, StringComparer.Ordinal))
    {
        Say($"  '{avd}' is not one of the AVDs above.");
        return 1;
    }

    if (running.Count > 0)
    {
        Say("");
        Say("An emulator is already running, so this one would not start cleanly or would not be the one measured.");
        Say("  Run again with --stop-running to shut it down first (it saves its quick-boot snapshot),");
        Say($"  or --attach {running[0].Discovery.GrpcPort?.ToString(CultureInfo.InvariantCulture) ?? "<port>"} to measure it as it is (no boot time).");
        return 1;
    }

    var port = int.Parse(Value("--port") ?? "8554", CultureInfo.InvariantCulture);
    var arguments = EmulatorCommand.Arguments(avd, port, noWindow: Flag("--no-window"));
    Say("");
    Say($"Starting: emulator {string.Join(' ', arguments)}");

    var info = new ProcessStartInfo(emulator) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }

    var launched = DateTime.UtcNow.AddSeconds(-2);
    using var process = Process.Start(info) ?? throw new InvalidOperationException("the emulator did not start");
    var output = new List<string>();
    process.OutputDataReceived += (_, e) => Keep(output, e.Data);
    process.ErrorDataReceived += (_, e) => Keep(output, e.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    (EmulatorDiscovery Discovery, int? Pid)? found = null;

    try
    {
        found = await DiscoverAsync(output, launched, profile, process).ConfigureAwait(false);

        var code = found is { } f
            ? await ProbeAsync(process, f.Discovery.GrpcPort!.Value, f.Discovery.GrpcToken, f.Pid).ConfigureAwait(false)
            : 1;

        Say("");
        Say("What the emulator printed (warnings and errors, at most 40 lines):");
        lock (output)
        {
            foreach (var line in output.Where(l => new[] { "WARN", "ERROR", "FATAL", "grpc", "Advertising", "token", "jwk" }
                .Any(word => l.Contains(word, StringComparison.OrdinalIgnoreCase))).Take(40))
            {
                Say("  " + line);
            }
        }

        return code;
    }
    finally
    {
        if (!Flag("--keep"))
        {
            await ShutdownAsync(found?.Discovery.GrpcPort, found?.Discovery.GrpcToken, found?.Pid, process).ConfigureAwait(false);
        }
    }
}

// Emulators running now: discovery files whose process is alive and is an emulator.
static List<(int Pid, string Path, EmulatorDiscovery Discovery)> Running(string profile)
{
    var running = new List<(int, string, EmulatorDiscovery)>();

    foreach (var folder in EmulatorDiscovery.Folders(Environment.GetEnvironmentVariable, Path.GetTempPath(), profile).Where(Directory.Exists))
    {
        foreach (var path in Directory.EnumerateFiles(folder, "pid_*.ini"))
        {
            if (EmulatorDiscovery.PidOf(path) is { } pid && IsEmulator(pid))
            {
                try
                {
                    running.Add((pid, path, EmulatorDiscovery.Parse(File.ReadAllText(path))));
                }
                catch (IOException)
                {
                    // Being written or removed as we look; the next look will see it.
                }
            }
        }
    }

    return running;
}

static bool IsEmulator(int pid)
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

// Shut Android down the way closing the emulator does — through gRPC — so its quick-boot snapshot is
// saved; only if it is still there after a minute, kill the virtual machine and the launcher.
async Task ShutdownAsync(int? port, string? token, int? vmPid, Process? launcher)
{
    var watch = Stopwatch.StartNew();
    var asked = false;

    if (port is { } p)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{p}");
            var client = new EmulatorController.EmulatorControllerClient(channel);
            var headers = token is null ? new Metadata() : new Metadata { { "authorization", "Bearer " + token } };
            await client.setVmStateAsync(new VmRunState { State = VmRunState.Types.RunState.Shutdown }, headers, DateTime.UtcNow.AddSeconds(10)).ConfigureAwait(false);
            asked = true;
        }
        catch (RpcException e)
        {
            Say($"Shutting down over gRPC was refused ({e.StatusCode}); it will be stopped instead.");
        }
    }

    bool Alive() => (vmPid is { } pid && IsEmulator(pid)) || launcher is { HasExited: false };

    while (asked && Alive() && watch.Elapsed < TimeSpan.FromSeconds(60))
    {
        await Task.Delay(500).ConfigureAwait(false);
    }

    if (!Alive())
    {
        Say(asked
            ? $"Shut down cleanly in {watch.Elapsed.TotalSeconds:0} s (the quick-boot snapshot is saved on the way out)."
            : "The emulator had already stopped.");
        return;
    }

    Say("Stopping the emulator by force (no snapshot saved).");
    if (vmPid is { } vm)
    {
        try
        {
            using var process = Process.GetProcessById(vm);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    if (launcher is { HasExited: false })
    {
        launcher.Kill(entireProcessTree: true);
        await launcher.WaitForExitAsync().ConfigureAwait(false);
    }
}

async Task<(EmulatorDiscovery Discovery, int? Pid)?> DiscoverAsync(List<string> output, DateTime launched, string profile, Process launcher)
{
    var folders = EmulatorDiscovery.Folders(Environment.GetEnvironmentVariable, Path.GetTempPath(), profile);
    var deadline = Stopwatch.StartNew();

    while (deadline.Elapsed < TimeSpan.FromSeconds(90))
    {
        // First what the emulator says, then the newest file written since it started: the file is
        // named after the virtual machine's process, not the launcher's, so its name is not ours to know.
        string? advertised;
        lock (output)
        {
            advertised = EmulatorDiscovery.AdvertisedPath(output);
        }

        var path = advertised is not null && File.Exists(advertised)
            ? advertised
            : EmulatorDiscovery.Newest(
                folders.Where(Directory.Exists)
                    .SelectMany(f => Directory.EnumerateFiles(f, "pid_*.ini"))
                    .Select(f => (f, File.GetLastWriteTimeUtc(f))),
                launched);

        if (path is not null)
        {
            var discovery = EmulatorDiscovery.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));

            if (discovery.GrpcPort is not null)
            {
                Say($"Discovery file after {deadline.Elapsed.TotalSeconds:0.0} s ({(path == advertised ? "where the emulator said" : "the newest since it started")}): {path}");
                foreach (var line in discovery.Redacted())
                {
                    Say("  " + line);
                }

                return (discovery, EmulatorDiscovery.PidOf(path));
            }
        }

        // The launcher stays running while the emulator does; gone means it refused or failed.
        if (launcher.HasExited)
        {
            Say($"THE EMULATOR EXITED (code {launcher.ExitCode}) after {deadline.Elapsed.TotalSeconds:0.0} s, before saying where its gRPC is. Its reasons are below.");
            return null;
        }

        await Task.Delay(500).ConfigureAwait(false);
    }

    Say($"NO DISCOVERY FILE with a gRPC port in 90 s. Looked in: {string.Join(", ", folders)}" +
        $"{(folders.Any(Directory.Exists) ? "; files there: " + string.Join(", ", folders.Where(Directory.Exists).SelectMany(Directory.EnumerateFiles).Select(Path.GetFileName)) : "")}." +
        " It is not measured: whatever answers on the port may not be this emulator.");
    return null;
}

async Task<int> ProbeAsync(Process? process, int port, string? token, int? vmPid)
{
    using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
    var client = new EmulatorController.EmulatorControllerClient(channel);
    var headers = token is null ? new Metadata() : new Metadata { { "authorization", "Bearer " + token } };

    // ── Boot ──────────────────────────────────────────────────────────────────
    var boot = Stopwatch.StartNew();
    EmulatorStatus? status = null;
    string? lastError = null;

    while (boot.Elapsed < TimeSpan.FromMinutes(5))
    {
        try
        {
            status = await client.getStatusAsync(new Empty(), headers, DateTime.UtcNow.AddSeconds(5)).ConfigureAwait(false);
            if (status.Booted)
            {
                break;
            }
        }
        catch (RpcException e)
        {
            lastError = $"{e.StatusCode}: {e.Status.Detail}";

            if (e.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
            {
                Say($"gRPC REFUSED: {lastError}. The token was {(token is null ? "not found" : "sent and not accepted")}; see the discovery file above.");
                return 1;
            }
        }

        if (process is { HasExited: true })
        {
            Say($"THE EMULATOR EXITED (code {process.ExitCode}) before it booted. Last gRPC answer: {lastError ?? "none"}.");
            return 1;
        }

        await Task.Delay(1000).ConfigureAwait(false);
    }

    if (status is not { Booted: true })
    {
        Say($"NOT BOOTED after {boot.Elapsed.TotalSeconds:0} s. Last gRPC answer: {lastError ?? "none"}.");
        return 1;
    }

    Say("");
    Say($"Booted: {boot.Elapsed.TotalSeconds:0.0} s to answer booted (emulator {status.Version}).");
    foreach (var key in new[] { "hw.lcd.width", "hw.lcd.height", "hw.lcd.density", "tag.id" })
    {
        if (status.GuestConfig.TryGetValue(key, out var value) || status.PlatformConfig.TryGetValue(key, out value))
        {
            Say($"  {key} = {value}");
        }
    }

    var width = int.Parse(Value("--width") ?? "1824", CultureInfo.InvariantCulture);
    var height = int.Parse(Value("--height") ?? "1216", CultureInfo.InvariantCulture);

    // ── A still, so the report shows what the stage would ─────────────────────
    var still = await client.getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png, Width = (uint)width, Height = (uint)height }, headers).ConfigureAwait(false);
    var stillPath = ReportPath("png");
    await File.WriteAllBytesAsync(stillPath, still.Image_.ToByteArray()).ConfigureAwait(false);
    Say($"Screenshot: {still.Format.Width}×{still.Format.Height}, saved beside the report ({Path.GetFileName(stillPath)}).");

    // ── Frames: idle, then while a finger drags ──────────────────────────────
    var seconds = int.Parse(Value("--seconds") ?? "10", CultureInfo.InvariantCulture);
    var screen = (W: (int)still.Format.Width, H: (int)still.Format.Height);
    var device = (W: Config(status, "hw.lcd.width") ?? screen.W, H: Config(status, "hw.lcd.height") ?? screen.H);

    var cpuBefore = EmulatorCpu(vmPid);
    var idle = await MeasureFramesAsync(client, headers, width, height, TimeSpan.FromSeconds(Math.Max(3, seconds / 3)), null).ConfigureAwait(false);
    var dragging = await MeasureFramesAsync(client, headers, width, height, TimeSpan.FromSeconds(seconds), ct => DragAsync(client, headers, device, ct)).ConfigureAwait(false);
    var cpuAfter = EmulatorCpu(vmPid);

    Say("");
    Say($"Frames at {width}×{height} (RGB888 over gRPC):");
    Say($"  idle:     {idle.Meter.FramesPerSecond:0.0} fps, longest gap {idle.Meter.LongestGap.TotalMilliseconds:0} ms ({idle.Meter.Count} frames; an idle screen sends few — that is expected)");
    Say($"  dragging: {dragging.Meter.FramesPerSecond:0.0} fps, {dragging.Meter.MegabytesPerSecond:0.0} MB/s, longest gap {dragging.Meter.LongestGap.TotalMilliseconds:0} ms ({dragging.Meter.Count} frames)");
    Say($"  frame size: {dragging.Width}×{dragging.Height}");

    // ── How long a touch takes to show ───────────────────────────────────────
    var latency = await TouchLatencyAsync(client, headers, device, width, height).ConfigureAwait(false);
    Say($"Touch to next frame: {(latency is { } l ? $"{l.TotalMilliseconds:0} ms (median of 5)" : "no frame followed a touch")}");

    if (Flag("--tap"))
    {
        await TapAsync(client, headers, device.W / 2, device.H / 2).ConfigureAwait(false);
        Say("Tapped the middle of the screen once.");
    }

    // ── Resources ────────────────────────────────────────────────────────────
    var (memoryMb, processes) = EmulatorMemory(vmPid);
    var cpu = cpuBefore is { } a && cpuAfter is { } b
        ? (b.Cpu - a.Cpu).TotalSeconds / Math.Max((b.At - a.At).TotalSeconds, 1e-9) / Environment.ProcessorCount * 100
        : (double?)null;
    Say($"Emulator processes ({processes}): {memoryMb:0} MB working set; {(cpu is { } c ? $"{c:0}% of all cores while measuring" : "processor time not readable")}.");

    // ── GPS and audio ────────────────────────────────────────────────────────
    if (Value("--gps") is { } gps && gps.Split(',') is [var lat, var lon])
    {
        await client.setGpsAsync(new GpsState
        {
            Latitude = double.Parse(lat, CultureInfo.InvariantCulture),
            Longitude = double.Parse(lon, CultureInfo.InvariantCulture),
            Satellites = 10,
        }, headers).ConfigureAwait(false);
        var back = await client.getGpsAsync(new Empty(), headers).ConfigureAwait(false);
        var same = Math.Abs(back.Latitude - double.Parse(lat, CultureInfo.InvariantCulture)) < 1e-4
            && Math.Abs(back.Longitude - double.Parse(lon, CultureInfo.InvariantCulture)) < 1e-4;

        // Only whether it matched: a report is something people send, and a position is not for sending.
        Say($"GPS: set, and read back {(same ? "the same" : "DIFFERENT")}.");
    }

    var audio = await AudioAsync(client, headers).ConfigureAwait(false);
    Say($"Audio: {audio}");

    Say("");
    Say($"Done in {started.Elapsed.TotalSeconds:0} s.");
    return 0;
}

async Task<(FrameMeter Meter, uint Width, uint Height)> MeasureFramesAsync(
    EmulatorController.EmulatorControllerClient client, Metadata headers, int width, int height, TimeSpan length, Func<CancellationToken, Task>? alongside)
{
    var meter = new FrameMeter();
    using var cancel = new CancellationTokenSource(length);
    var clock = Stopwatch.StartNew();
    uint w = 0, h = 0;
    var work = alongside?.Invoke(cancel.Token) ?? Task.CompletedTask;

    try
    {
        using var stream = client.streamScreenshot(
            new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgb888, Width = (uint)width, Height = (uint)height },
            headers, cancellationToken: cancel.Token);

        await foreach (var image in stream.ResponseStream.ReadAllAsync(cancel.Token).ConfigureAwait(false))
        {
            if (image.Image_.Length > 0)
            {
                meter.Add(clock.Elapsed, image.Image_.Length);
                (w, h) = (image.Format.Width, image.Format.Height);
            }
        }
    }
    catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled || cancel.IsCancellationRequested)
    {
    }
    catch (OperationCanceledException)
    {
    }

    try
    {
        await work.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }

    return (meter, w, h);
}

// Up and down the left third of the screen, slowly, so a list or the home screen moves the whole time.
async Task DragAsync(EmulatorController.EmulatorControllerClient client, Metadata headers, (int W, int H) device, CancellationToken ct)
{
    var x = device.W / 3;
    var y = device.H / 2;
    var step = Math.Max(device.H / 40, 4);
    var down = true;

    while (!ct.IsCancellationRequested)
    {
        await TouchAsync(client, headers, x, device.H * 3 / 4, 1).ConfigureAwait(false);
        for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
        {
            y = down ? (device.H * 3 / 4) - (i * step) : (device.H / 4) + (i * step);
            await TouchAsync(client, headers, x, y, 1).ConfigureAwait(false);
            await Task.Delay(16, CancellationToken.None).ConfigureAwait(false);
        }

        await TouchAsync(client, headers, x, y, 0).ConfigureAwait(false);
        down = !down;
    }
}

async Task<TimeSpan?> TouchLatencyAsync(EmulatorController.EmulatorControllerClient client, Metadata headers, (int W, int H) device, int width, int height)
{
    var results = new List<TimeSpan>();

    for (var i = 0; i < 5; i++)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var stream = client.streamScreenshot(
            new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgb888, Width = (uint)width, Height = (uint)height },
            headers, cancellationToken: cancel.Token);

        try
        {
            // Let the stream settle, then touch and time the first new frame.
            var quiet = Task.Delay(300, CancellationToken.None);
            var reader = stream.ResponseStream;
            var pending = reader.MoveNext(cancel.Token);

            while (await Task.WhenAny(pending, quiet).ConfigureAwait(false) == pending && await pending.ConfigureAwait(false))
            {
                pending = reader.MoveNext(cancel.Token);
            }

            var touched = Stopwatch.StartNew();
            await TouchAsync(client, headers, device.W / 3, (device.H / 3) + (i * 10), 1).ConfigureAwait(false);
            await TouchAsync(client, headers, device.W / 3, (device.H / 3) + (i * 10) + 40, 1).ConfigureAwait(false);

            if (await pending.ConfigureAwait(false))
            {
                results.Add(touched.Elapsed);
            }
        }
        catch (Exception e) when (e is RpcException or OperationCanceledException)
        {
        }
        finally
        {
            await TouchAsync(client, headers, device.W / 3, device.H / 3, 0).ConfigureAwait(false);
        }
    }

    return results.Count == 0 ? null : results.Order().ElementAt(results.Count / 2);
}

Task TapAsync(EmulatorController.EmulatorControllerClient client, Metadata headers, int x, int y) =>
    TouchAsync(client, headers, x, y, 1).ContinueWith(_ => TouchAsync(client, headers, x, y, 0), TaskScheduler.Default).Unwrap();

async Task TouchAsync(EmulatorController.EmulatorControllerClient client, Metadata headers, int x, int y, int pressure)
{
    var touch = new TouchEvent();
    touch.Touches.Add(new Touch { X = x, Y = y, Identifier = 1, Pressure = pressure });

    try
    {
        await client.sendTouchAsync(touch, headers).ConfigureAwait(false);
    }
    catch (RpcException)
    {
        // A dropped touch during measurement is not worth stopping the probe for.
    }
}

async Task<string> AudioAsync(EmulatorController.EmulatorControllerClient client, Metadata headers)
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    long bytes = 0;
    var packets = 0;

    try
    {
        using var stream = client.streamAudio(
            new AudioFormat { SamplingRate = 48000, Channels = AudioFormat.Types.Channels.Stereo, Format = AudioFormat.Types.SampleFormat.AudFmtS16 },
            headers, cancellationToken: cancel.Token);

        await foreach (var packet in stream.ResponseStream.ReadAllAsync(cancel.Token).ConfigureAwait(false))
        {
            bytes += packet.Audio.Length;
            packets++;
        }
    }
    catch (Exception e) when (e is RpcException or OperationCanceledException)
    {
    }

    return packets == 0
        ? "no packets in 3 s (Android may simply be silent; play something and run with --keep to check by ear)"
        : string.Create(CultureInfo.InvariantCulture, $"{packets} packets, {bytes / 1024} KB in 3 s at 48 kHz stereo");
}

static int? Config(EmulatorStatus status, string key) =>
    (status.GuestConfig.TryGetValue(key, out var value) || status.PlatformConfig.TryGetValue(key, out value))
    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

static (DateTime At, TimeSpan Cpu)? EmulatorCpu(int? vmPid)
{
    try
    {
        var processes = EmulatorProcesses(vmPid);
        return processes.Count == 0 ? null : (DateTime.UtcNow, processes.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.TotalProcessorTime));
    }
    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

static (double Mb, int Count) EmulatorMemory(int? vmPid)
{
    var processes = EmulatorProcesses(vmPid);
    return (processes.Sum(p => p.WorkingSet64) / 1_048_576.0, processes.Count);
}

// The emulator program starts the virtual machine as a child, qemu-system-x86_64, which is where the
// memory and processor time go; both are counted.
static List<Process> EmulatorProcesses(int? vmPid)
{
    // The virtual machine itself when its discovery file named it; otherwise every emulator process.
    if (vmPid is { } pid)
    {
        try
        {
            return [Process.GetProcessById(pid)];
        }
        catch (ArgumentException)
        {
        }
    }

    return [.. Process.GetProcesses().Where(p => p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)
        || p.ProcessName.Equals("emulator", StringComparison.OrdinalIgnoreCase))];
}

static AvdInfo? ReadAvd(string avdHome, string name)
{
    var direct = Path.Combine(avdHome, name + ".avd", "config.ini");
    var pointer = Path.Combine(avdHome, name + ".ini");
    var path = File.Exists(direct) ? direct : null;

    if (path is null && File.Exists(pointer))
    {
        var folder = File.ReadAllLines(pointer).FirstOrDefault(l => l.StartsWith("path=", StringComparison.Ordinal))?[5..].Trim();
        path = folder is null ? null : Path.Combine(folder, "config.ini");
    }

    return path is not null && File.Exists(path) ? AvdInfo.Parse(name, File.ReadAllText(path)) : null;
}

static async Task<string> CaptureAsync(string program, string argument)
{
    var info = new ProcessStartInfo(program, argument) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

    try
    {
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            return "(did not finish in 30 s)";
        }

        return (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
    }
    catch (System.ComponentModel.Win32Exception e)
    {
        return $"(would not run: {e.Message})";
    }
}

static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

static void Keep(List<string> lines, string? line)
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

void Say(string line)
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

// The folder is made here, not when the report is saved: the screenshot is written beside it first.
string ReportPath(string extension)
{
    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "androidprobe");
    Directory.CreateDirectory(folder);
    return Path.Combine(folder, "probe-" + stamp + "." + extension);
}

void Save()
{
    try
    {
        var path = ReportPath("txt");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"Report: {path}");
    }
    catch (IOException e)
    {
        Console.Error.WriteLine($"The report could not be saved: {e.Message}");
    }
}

bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

string? Value(string name)
{
    var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

void Usage()
{
    Console.WriteLine("""
        AndroidProbe — can this tablet run Android as its own device on the stage? (ADR-0075)

          AndroidProbe                        find the SDK, check virtualisation, list the AVDs
          AndroidProbe --avd <name>           start that AVD hidden with gRPC, and measure it
          AndroidProbe --attach <port>        measure an emulator already running (--token <t> if it needs one)

          --sdk <folder>        the Android SDK, if not in ANDROID_HOME or Android Studio's place
          --port <n>            the gRPC port to ask for (8554)
          --width/--height <px> the frame size to ask for (1824×1216, about the stage)
          --seconds <n>         how long to measure while dragging (10)
          --no-window           start with -no-window instead of -qt-hide-window
          --tap                 tap the middle of the screen once
          --gps <lat,lon>       set a GPS position and read it back
          --keep                leave the emulator running afterwards
          --stop-running        shut down any emulator already running first (saves its snapshot)

        The report goes to %LOCALAPPDATA%\DashDeck\androidprobe\. Nothing is installed or changed.
        """);
}
