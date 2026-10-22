using System.Diagnostics;
using DashDeck.Core.Emulation;
using DashDeck.Core.Location;

namespace DashDeck.Emulator;

/// <summary>Where a session is.</summary>
public enum EmulatorState
{
    Stopped,
    Starting,
    Booting,
    Running,
    Stopping,
    Failed,
}

/// <summary>
/// One AVD running as the stage's Android (ADR-0075): started — or found already running — booted,
/// drawn, touched, told where the truck is, and shut down cleanly.
/// </summary>
/// <remarks>
/// <b>One at a time.</b> An emulator of another AVD left running is shut down first: two would not
/// fit in the Surface's memory beside the dash, and they would both want the gRPC port. One of the
/// same AVD is attached to rather than started over, which the emulator would refuse anyway.
/// <para>
/// Nothing here throws to the caller after construction: a failure is a <see cref="State"/> and a
/// sentence in <see cref="Detail"/>, so the stage can say it.
/// </para>
/// </remarks>
public sealed class EmulatorSession : IAsyncDisposable
{
    private readonly string _sdk;
    private readonly string? _skin;
    private readonly int _port;
    private readonly CancellationTokenSource _life = new();
    private readonly CancellationToken _lifetime;
    private EmulatorClient? _client;
    private Process? _launcher;
    private int? _pid;
    private EmulatorDiscovery? _discovery;
    private Task? _starting;
    private Func<IReadOnlyList<string>>? _output;
    private volatile int _quarterTurns;
    private volatile AndroidFit? _fit;
    private AndroidFit? _wantedFit;
    // One ADB command at a time, the fit's and the car feed's alike.
    private readonly SemaphoreSlim _adb = new(1, 1);
    private CarState? _carTold;
    private VhalRoute? _route;

    /// <param name="resources">
    /// Memory and cores to start with; by default what this tablet can spare
    /// (<see cref="EmulatorResources"/>). A change from the last start costs one cold boot, since the
    /// quick-boot snapshot was saved with the old memory.
    /// </param>
    public EmulatorSession(string sdk, AvdInfo avd, string? skin = null, int port = 8554, (int? MemoryMb, int? Cores)? resources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdk);
        ArgumentNullException.ThrowIfNull(avd);

        _sdk = sdk;
        Avd = avd;
        _skin = skin;
        _port = port;
        _lifetime = _life.Token;
        Resources = resources ?? EmulatorResources.For(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount);
    }

    /// <summary>
    /// What Android was last told its screen is (<see cref="FitAsync"/>), or null when it has not been:
    /// frames are cropped to it and touch goes back through it.
    /// </summary>
    public AndroidFit? Fit => _fit;

    /// <summary>Why the last fit could not be made, or null.</summary>
    public string? FitProblem { get; private set; }

    /// <summary>Why the last car data was not taken, or null.</summary>
    public string? CarProblem { get; private set; }

    /// <summary>What Android was last told about the truck, or null before anything was.</summary>
    public CarState? CarTold => _carTold;

    /// <summary>
    /// What draws Android: as Android itself says (<see cref="ReadRendererAsync"/>), else as the
    /// emulator said when it started, else null.
    /// </summary>
    public string? Renderer =>
        GuestRenderer
        ?? (Machine is { } machine ? EmulatorGpu.FromConfig(machine.Config) : null)
        ?? (_output is { } output ? EmulatorGpu.Renderer(output()) : null);

    /// <summary>What the running emulator says it is — GPU mode, cores, memory — or null before it is running.</summary>
    public EmulatorMachine? Machine { get; private set; }

    /// <summary>What came back when Android was asked what draws it and did not say, or null.</summary>
    public string? RendererProblem { get; private set; }

    /// <summary>What Android said draws it (<see cref="EmulatorGpu.FromGles"/>), or null until it has.</summary>
    public string? GuestRenderer { get; private set; }

    /// <summary>True when the GPU asked for was refused and the emulator was started its own way.</summary>
    public bool GpuFellBack { get; private set; }

    /// <summary>The emulator's <c>-gpu</c> mode to start with, and whether to fall back from it.</summary>
    public GpuChoice Gpu { get; init; } = GpuChoice.Emulator;

    /// <summary>The memory and cores the emulator is started with; null for the AVD's own.</summary>
    public (int? MemoryMb, int? Cores) Resources { get; }

    /// <summary>The AVD this session runs.</summary>
    public AvdInfo Avd { get; }

    public EmulatorState State { get; private set; } = EmulatorState.Stopped;

    /// <summary>What it is doing, or why it stopped, in one sentence.</summary>
    public string Detail { get; private set; } = "not started";

    /// <summary>The device's own screen, which touch is given in. 0 until it is running.</summary>
    public int DeviceWidth { get; private set; }

    public int DeviceHeight { get; private set; }

    /// <summary>How long the last start took to boot, or null.</summary>
    public TimeSpan? BootTime { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> or <see cref="Detail"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Start it, once. Later calls return the same start.</summary>
    public Task StartAsync() => _starting ??= Task.Run(RunStartAsync);

    private async Task RunStartAsync()
    {
        var ct = _life.Token;
        var watch = Stopwatch.StartNew();

        try
        {
            Set(EmulatorState.Starting, $"starting {Avd.Name}");

            var running = EmulatorLauncher.Running();
            var mine = running.FirstOrDefault(r => r.AvdId == Avd.Name && r.Discovery.GrpcPort is not null);

            foreach (var other in running.Where(r => r != mine))
            {
                Set(EmulatorState.Starting, $"shutting down {other.Discovery.AvdName ?? "another emulator"} first");
                await EmulatorLauncher.StopAsync(other.Discovery.GrpcPort, other.Discovery.GrpcToken, other.Pid, null).ConfigureAwait(false);
            }

            if (mine is not null)
            {
                _discovery = mine.Discovery;
                _pid = mine.Pid;
                Set(EmulatorState.Booting, $"{Avd.Name} was already running; joining it");
            }
            else
            {
                var dns = HostNetwork.DnsServers();
                var launch = await EmulatorLauncher.StartAsync(
                    _sdk, Avd.Name, _port, _skin, line => Set(EmulatorState.Starting, line), ct, Resources.MemoryMb, Resources.Cores,
                    dns, Gpu.Mode).ConfigureAwait(false);

                // The host's GPU refused: start again the emulator's way, which at least draws.
                if (launch.Failure is { } refused && Gpu is { FallBack: true, Mode: { } mode })
                {
                    Set(EmulatorState.Starting, $"the emulator would not draw with -gpu {mode} ({refused}); starting it its own way");
                    launch = await EmulatorLauncher.StartAsync(
                        _sdk, Avd.Name, _port, _skin, line => Set(EmulatorState.Starting, line), ct, Resources.MemoryMb, Resources.Cores,
                        dns, null).ConfigureAwait(false);
                    GpuFellBack = true;
                }

                if (launch.Failure is { } failure)
                {
                    Set(EmulatorState.Failed, failure);
                    return;
                }

                _launcher = launch.Launcher;
                _output = launch.Output;
                _discovery = launch.Discovery;
                _pid = launch.Pid;
                Set(EmulatorState.Booting, $"booting {Avd.Name}");
            }

            _client = new EmulatorClient(_discovery!.GrpcPort!.Value, _discovery.GrpcToken);

            if (!await _client.WaitForBootAsync(TimeSpan.FromMinutes(5), () => _launcher is { HasExited: true }, ct).ConfigureAwait(false))
            {
                Set(EmulatorState.Failed, _launcher is { HasExited: true } ? "the emulator stopped while booting" : "Android did not finish booting in 5 minutes");
                return;
            }

            (DeviceWidth, DeviceHeight) = await _client.ScreenSizeAsync(ct).ConfigureAwait(false);
            Machine = await _client.MachineAsync(ct).ConfigureAwait(false);
            BootTime = watch.Elapsed;
            Set(EmulatorState.Running, $"{Avd.Name} running ({DeviceWidth}×{DeviceHeight}, ready in {watch.Elapsed.TotalSeconds:0.0} s)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (EmulatorRefusedException e)
        {
            Set(EmulatorState.Failed, e.Message);
        }
        catch (Exception e) when (e is IOException or Grpc.Core.RpcException or InvalidOperationException)
        {
            Set(EmulatorState.Failed, $"{Avd.Name} could not be started: {e.Message}");
        }
    }

    /// <summary>Frames until cancelled, once running; returns at once otherwise.</summary>
    public async Task StreamAsync(int width, int height, Action<EmulatorFrame> onFrame, CancellationToken ct)
    {
        if (State != EmulatorState.Running || _client is not { } client)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(onFrame);

        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        await client.StreamFramesAsync(
            width,
            height,
            frame =>
            {
                _quarterTurns = frame.QuarterTurns;
                onFrame(frame);
            },
            both.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Turn the device: 0 its own way up, 90 a quarter turn (<see cref="AndroidChoice.TurnFor"/>).
    /// Touch follows the frames, which say how they came, so nothing else changes here.
    /// </summary>
    public Task RotateAsync(int degrees) =>
        State == EmulatorState.Running && _client is { } client ? client.RotateAsync(degrees) : Task.CompletedTask;

    /// <summary>
    /// Tell Android the shape and density of the region it is drawn in (<c>wm size</c>,
    /// <c>wm density</c> through the SDK's ADB). Only the newest of several asked for at once is
    /// made. False when it could not be — ADB offline or missing — and <see cref="FitProblem"/> says
    /// why; the picture is then shown whole, with its bars, as before.
    /// </summary>
    public async Task<bool> FitAsync(AndroidFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);

        if (State != EmulatorState.Running || _discovery?.AdbSerial is not { } serial)
        {
            return false;
        }

        var adb = AndroidSdk.AdbPath(_sdk);
        if (!File.Exists(adb))
        {
            FitProblem = "the SDK has no adb";
            return false;
        }

        _wantedFit = fit;

        try
        {
            await _adb.WaitAsync(_lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!ReferenceEquals(_wantedFit, fit))
            {
                return false;   // a newer one is waiting
            }

            if (fit == _fit)
            {
                return true;
            }

            foreach (var command in fit.AdbCommands(serial))
            {
                if (!await RunWithRetriesAsync(adb, command).ConfigureAwait(false))
                {
                    return false;
                }
            }

            FitProblem = null;
            _fit = fit;
            return true;
        }
        finally
        {
            _adb.Release();
        }
    }

    /// <summary>
    /// Tell Android what has changed about the truck (<see cref="CarFeed"/>): Automotive through its
    /// vehicle HAL, a tablet only whether it is night. One shell call for all of it. Skipped while
    /// another ADB command runs — the next beat brings the newest state anyway.
    /// </summary>
    public async Task FeedAsync(CarState next)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (State != EmulatorState.Running || _discovery?.AdbSerial is not { } serial || !File.Exists(AndroidSdk.AdbPath(_sdk)))
        {
            return;
        }

        if (!await _adb.WaitAsync(0, _lifetime).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var adb = AndroidSdk.AdbPath(_sdk);

            // Which way into the vehicle HAL this image takes, asked once.
            if (Avd.Flavour == AndroidFlavour.Automotive && _route is null)
            {
                var (_, answer) = await Adb.RunAsync(adb, ["-s", serial, "shell", CarFeed.StoreProbe], TimeSpan.FromSeconds(15), _lifetime).ConfigureAwait(false);
                _route = CarFeed.StoreAnswers(answer) ? VhalRoute.Store : VhalRoute.Event;
            }

            var route = _route ?? VhalRoute.Store;
            var (commands, told) = CarFeed.Changes(_carTold, next, Avd.Flavour, route);
            if (commands.Count == 0)
            {
                return;
            }

            var (done, output) = await Adb.RunAsync(adb, ["-s", serial, "shell", string.Join("; ", commands)], TimeSpan.FromSeconds(15), _lifetime).ConfigureAwait(false);
            if (done && !CarFeed.Refused(output))
            {
                _carTold = told;
                CarProblem = null;
                return;
            }

            CarProblem = $"{route.ToString().ToLowerInvariant()}: {AdbAnswers.State(output)}";

            // The HAL's store refused: the car service's events are the other way in.
            if (route == VhalRoute.Store && CarFeed.Refused(output))
            {
                _route = VhalRoute.Event;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _adb.Release();
        }
    }

    /// <summary>
    /// Ask Android what draws it, through ADB, until it says. Skipped while another ADB command runs.
    /// </summary>
    public async Task ReadRendererAsync()
    {
        if (GuestRenderer is not null || State != EmulatorState.Running || _discovery?.AdbSerial is not { } serial
            || !File.Exists(AndroidSdk.AdbPath(_sdk)))
        {
            return;
        }

        if (!await _adb.WaitAsync(0, _lifetime).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var (_, output) = await Adb.RunAsync(AndroidSdk.AdbPath(_sdk), ["-s", serial, "shell", EmulatorGpu.GlesProbe], TimeSpan.FromSeconds(15), _lifetime)
                .ConfigureAwait(false);
            GuestRenderer = EmulatorGpu.FromGles(output);
            RendererProblem = GuestRenderer is null
                ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } first
                    ? first.Length > 80 ? first[..80] + "…" : first
                    : "no GLES line"
                : null;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _adb.Release();
        }
    }

    /// <summary>Let Android hear the tablet's microphone, or not.</summary>
    public Task<bool> SetMicrophoneAsync(bool hear) =>
        State == EmulatorState.Running && _client is { } client ? client.SetMicrophoneAsync(hear) : Task.FromResult(false);

    /// <summary>
    /// A just-booted device can read as offline for a moment, and the first command starts ADB's
    /// server, so a failure is tried twice more before it counts.
    /// </summary>
    private async Task<bool> RunWithRetriesAsync(string adb, IReadOnlyList<string> command)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (done, output) = await Adb.RunAsync(adb, command, TimeSpan.FromSeconds(15), _lifetime).ConfigureAwait(false);
            if (done)
            {
                return true;
            }

            if (attempt == 3 || _lifetime.IsCancellationRequested)
            {
                FitProblem = $"{string.Join(' ', command.Skip(3))}: {AdbAnswers.State(output)}";
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A finger, as fractions of the picture as drawn — through the fit onto the whole frame, then
    /// turned back by the last frame's rotation onto the device's own screen. Off the picture, only a
    /// lift is sent.
    /// </summary>
    public Task TouchAsync(double x, double y, bool down)
    {
        if (State != EmulatorState.Running || _client is not { } client)
        {
            return Task.CompletedTask;
        }

        var (onFrameX, onFrameY) = _fit is { } fit ? fit.ToFrame(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)) : (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        var at = AndroidChoice.ToDevice(onFrameX, onFrameY, DeviceWidth, DeviceHeight, _quarterTurns);
        var inside = x is >= 0 and <= 1 && y is >= 0 and <= 1;

        return at is { } point && (inside || !down) ? client.TouchAsync(point.X, point.Y, down) : Task.CompletedTask;
    }

    /// <summary>The truck's position, for Android's own location.</summary>
    public Task SetPositionAsync(LocationFix fix) =>
        State == EmulatorState.Running && _client is { } client ? client.SetPositionAsync(fix) : Task.CompletedTask;

    /// <summary>One line for <c>--shot</c> and the stage.</summary>
    public string Describe() => $"{AndroidChoice.Label(Avd.Flavour).ToLowerInvariant()} · {State.ToString().ToLowerInvariant()} · {Detail}";

    /// <summary>Shut Android down cleanly, saving its snapshot, and stop.</summary>
    public async ValueTask DisposeAsync()
    {
        if (State is EmulatorState.Stopped or EmulatorState.Stopping)
        {
            return;
        }

        Set(EmulatorState.Stopping, $"shutting {Avd.Name} down");
        await _life.CancelAsync().ConfigureAwait(false);

        if (_starting is { } starting)
        {
            try
            {
                await starting.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await EmulatorLauncher.StopAsync(_discovery?.GrpcPort, _discovery?.GrpcToken, _pid, _launcher).ConfigureAwait(false);
        _client?.Dispose();
        _launcher?.Dispose();
        _life.Dispose();
        Set(EmulatorState.Stopped, "stopped");
    }

    /// <summary>
    /// For the dash closing: ask Android to shut down, and do not wait for it. The emulator finishes
    /// on its own and saves its snapshot — the dash has ten seconds to close and Android may take
    /// five. Only if it cannot be asked is it stopped by force, so it is never left running.
    /// </summary>
    public async Task LetGoAsync()
    {
        if (State is EmulatorState.Stopped)
        {
            return;
        }

        await _life.CancelAsync().ConfigureAwait(false);

        var asked = _client is { } client && await client.ShutdownAsync().ConfigureAwait(false);
        if (!asked)
        {
            await EmulatorLauncher.StopAsync(null, null, _pid, _launcher).ConfigureAwait(false);
        }

        _client?.Dispose();
        Set(EmulatorState.Stopped, asked ? "asked to shut down" : "stopped");
    }

    private void Set(EmulatorState state, string detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }
}
