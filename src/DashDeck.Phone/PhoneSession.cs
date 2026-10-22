using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DashDeck.Core.Phone;

namespace DashDeck.Phone;

/// <summary>Where a phone's mirror is.</summary>
public enum PhoneState
{
    Stopped,
    Starting,
    Connecting,
    Running,
    Stopping,
    Failed,
}

/// <summary>
/// A real phone on the stage, mirrored with the owner's scrcpy (ADR-0076): its screen as H.264 to the
/// dash's decoder, touch and keys back, and its screen told the region's shape.
/// </summary>
/// <remarks>
/// The phone does all of Android's drawing; the tablet only decodes video, which its GPU does in
/// hardware. That is the whole point: the Android Emulator this replaced (ADR-0075) kept the tablet's
/// processor at 85 % and drew 9 frames a second.
/// <para>
/// <b>Started as scrcpy's own client starts it:</b> the installed server pushed to the phone, a
/// forward tunnel (the phone listens, so no firewall rule on the tablet), the server started with the
/// installed version, then a video socket — which opens with a dummy byte when the server is there —
/// and a control socket. No sound yet: a phone plays through its own speaker or Bluetooth.
/// </para>
/// <para>
/// <b>The phone is put back.</b> Its screen is told the region's size and density while it is
/// mirrored (<c>wm size</c>, <c>wm density</c>), and both are reset when the session stops — also when
/// the dash closes. If the mirror ends any other way — the cable pulled, the Wi-Fi lost — a watchdog
/// left on the phone puts them back (<see cref="PhoneShell.Watchdog"/>). Its own display is turned off
/// while mirrored, and scrcpy's clean-up turns it back on.
/// </para>
/// </remarks>
public sealed class PhoneSession : IAsyncDisposable
{
    private const int MaxFps = 60;
    private const int BitRate = 8_000_000;

    private readonly string _adb;
    private readonly ScrcpyInstall _scrcpy;
    private readonly int _maxSide;
    private readonly CancellationTokenSource _life = new();
    private readonly CancellationToken _lifetime;
    private readonly SemaphoreSlim _adbGate = new(1, 1);
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly List<string> _serverOutput = [];
    private Process? _server;
    private TcpClient? _videoSocket;
    private TcpClient? _controlSocket;
    private int _port;
    private bool _fingerDown;
    private volatile bool _showing = true;
    private bool _fitMade;
    private bool? _nightTold;
    private Task? _starting;

    /// <param name="adb">The <c>adb</c> to use — scrcpy's own, so the dash and scrcpy share one ADB server.</param>
    /// <param name="maxSide">The longest side the phone's video may have.</param>
    public PhoneSession(string adb, AdbDevice phone, ScrcpyInstall scrcpy, int maxSide = 1920)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adb);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(scrcpy);

        _adb = adb;
        Phone = phone;
        _scrcpy = scrcpy;
        _maxSide = maxSide;
        _lifetime = _life.Token;
    }

    public AdbDevice Phone { get; }

    public PhoneState State { get; private set; } = PhoneState.Stopped;

    /// <summary>What it is doing, or why it stopped, in one sentence.</summary>
    public string Detail { get; private set; } = "not started";

    /// <summary>The picture's size as the phone last sent it — what touch is given in. 0 until it has.</summary>
    public int VideoWidth { get; private set; }

    public int VideoHeight { get; private set; }

    /// <summary>The scrcpy version in use.</summary>
    public string Version => _scrcpy.Version.Text;

    /// <summary>What the phone's screen was last told (<see cref="AndroidFit.ForMirror"/>), or null.</summary>
    public AndroidFit? Fit { get; private set; }

    /// <summary>Why the last fit could not be made, or null.</summary>
    public string? FitProblem { get; private set; }

    /// <summary>Why night mode could not be told, or null.</summary>
    public string? NightProblem { get; private set; }

    /// <summary>Video packets received — the phone sends one per frame it draws.</summary>
    public long Packets { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> or <see cref="Detail"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on the reading thread with H.264 to decode, Annex-B — only while <see cref="Showing"/>.</summary>
    public event Action<ReadOnlyMemory<byte>>? Video;

    /// <summary>The picture's size changed (the phone rotated, or its screen was told a new shape).</summary>
    public event Action<int, int>? SizeChanged;

    /// <summary>
    /// Whether the picture is being looked at. Hidden, video is dropped rather than decoded; shown
    /// again, the phone is asked for a fresh key frame so the decoder has something whole to start from.
    /// </summary>
    public bool Showing
    {
        get => _showing;
        set
        {
            if (value && !_showing)
            {
                _ = SendAsync(ScrcpyControl.Reset());
            }

            _showing = value;
        }
    }

    /// <summary>Start it, once. Later calls return the same start.</summary>
    public Task StartAsync() => _starting ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var ct = _lifetime;

        try
        {
            Set(PhoneState.Starting, $"putting scrcpy {Version}'s server on the {Phone.Name}");
            if (await AdbAsync(["push", _scrcpy.ServerPath, ScrcpyServer.DevicePath], TimeSpan.FromSeconds(60)) is { } pushFailed)
            {
                Set(PhoneState.Failed, $"scrcpy's server could not be put on the phone: {pushFailed}");
                return;
            }

            var scid = ScrcpyServer.NewScid(Random.Shared);
            _port = FreePort();
            if (await AdbAsync(["forward", string.Create(CultureInfo.InvariantCulture, $"tcp:{_port}"), "localabstract:" + ScrcpyServer.SocketName(scid)], TimeSpan.FromSeconds(15)) is { } forwardFailed)
            {
                Set(PhoneState.Failed, $"the tunnel to the phone could not be made: {forwardFailed}");
                return;
            }

            Set(PhoneState.Starting, $"starting scrcpy {Version} on the {Phone.Name}");
            StartServer(scid);

            _videoSocket = await ConnectAsync(firstSocket: true, ct).ConfigureAwait(false);
            if (_videoSocket is null)
            {
                var said = Said();
                Set(PhoneState.Failed, said is null ? "scrcpy's server did not answer on the phone" : $"scrcpy's server stopped: {said}");
                return;
            }

            _controlSocket = await ConnectAsync(firstSocket: false, ct).ConfigureAwait(false);
            if (_controlSocket is null)
            {
                Set(PhoneState.Failed, "scrcpy's control channel did not open");
                return;
            }

            _ = DrainAsync(_controlSocket, ct);
            Set(PhoneState.Connecting, $"waiting for the {Phone.Name}'s picture");

            await ReadVideoAsync(_videoSocket.GetStream(), ct).ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                Set(PhoneState.Failed, "the phone stopped sending its screen — unplugged, or locked?");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!ct.IsCancellationRequested)
            {
                Set(PhoneState.Failed, $"the phone's mirror stopped: {Said() ?? e.Message}");
            }
        }
    }

    private void StartServer(int scid)
    {
        var info = new ProcessStartInfo(_adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in (string[])["-s", Phone.Serial, "shell", .. ScrcpyServer.Command(_scrcpy.Version, scid, _maxSide, MaxFps, BitRate)])
        {
            info.ArgumentList.Add(argument);
        }

        _server = Process.Start(info) ?? throw new InvalidOperationException("adb did not start");
        _server.OutputDataReceived += (_, e) => Keep(e.Data);
        _server.ErrorDataReceived += (_, e) => Keep(e.Data);
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
    }

    /// <summary>
    /// A socket through the tunnel. The first must bring the dummy byte, which says the server is
    /// listening — a forward tunnel connects whether it is or not. Tried for fifteen seconds while the
    /// server starts.
    /// </summary>
    private async Task<TcpClient?> ConnectAsync(bool firstSocket, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < TimeSpan.FromSeconds(15) && !ct.IsCancellationRequested)
        {
            if (_server is { HasExited: true })
            {
                return null;
            }

            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, _port, ct).ConfigureAwait(false);

                if (!firstSocket)
                {
                    return client;
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(2));
                var dummy = new byte[1];
                if (await client.GetStream().ReadAsync(dummy, wait.Token).ConfigureAwait(false) == 1)
                {
                    return client;
                }
            }
            catch (Exception e) when (e is SocketException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
            }

            client.Dispose();
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task ReadVideoAsync(Stream video, CancellationToken ct)
    {
        await foreach (var item in ScrcpyVideo.ReadAsync(video, _scrcpy.Version, ct).ConfigureAwait(false))
        {
            switch (item)
            {
                case ScrcpyItem.Size size:
                    VideoWidth = size.Width;
                    VideoHeight = size.Height;
                    SizeChanged?.Invoke(size.Width, size.Height);
                    if (State != PhoneState.Running)
                    {
                        Set(PhoneState.Running, $"{Phone.Name} mirrored with scrcpy {Version} ({size.Width}×{size.Height})");

                        // Its own screen off: it is behind the dash, and it saves the battery and the heat.
                        await SendAsync(ScrcpyControl.DisplayPower(false)).ConfigureAwait(false);
                    }

                    break;

                case ScrcpyItem.Packet packet:
                    Packets++;
                    if (_showing)
                    {
                        Video?.Invoke(packet.Data);
                    }

                    break;
            }
        }
    }

    /// <summary>What the phone sends back on the control socket — clipboard and the like — read and dropped.</summary>
    private static async Task DrainAsync(TcpClient control, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (await control.GetStream().ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// A finger, as fractions of the picture as drawn: down, moved while down, or lifted. The picture
    /// is the phone's screen edge to edge, so the fractions are simply of its size.
    /// </summary>
    public Task TouchAsync(double x, double y, bool down)
    {
        if (State != PhoneState.Running || VideoWidth <= 0 || VideoHeight <= 0 || (!down && !_fingerDown))
        {
            return Task.CompletedTask;
        }

        var action = !down ? 1 : _fingerDown ? 2 : 0;
        _fingerDown = down;

        var px = Math.Clamp((int)Math.Round(x * VideoWidth), 0, VideoWidth - 1);
        var py = Math.Clamp((int)Math.Round(y * VideoHeight), 0, VideoHeight - 1);
        return SendAsync(ScrcpyControl.Touch(action, px, py, VideoWidth, VideoHeight));
    }

    /// <summary>Back, home or recent apps: the key down and up.</summary>
    public async Task<bool> PressAsync(AndroidKey key)
    {
        var code = ScrcpyControl.KeyCode(key);
        return await SendAsync(ScrcpyControl.Key(0, code)).ConfigureAwait(false)
            && await SendAsync(ScrcpyControl.Key(1, code)).ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(byte[] message)
    {
        if (_controlSocket is not { Connected: true } control)
        {
            return false;
        }

        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await control.GetStream().WriteAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <summary>
    /// Tell the phone's screen the region's size and density. scrcpy follows the new size by itself,
    /// starting a new capture session. On Android 12 and later, apps that ask for one way round are
    /// letterboxed rather than turning the screen (best effort: older Androids have no such setting).
    /// </summary>
    public async Task<bool> FitAsync(AndroidFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);

        if (State != PhoneState.Running)
        {
            return false;
        }

        foreach (var command in fit.AdbCommands(Phone.Serial))
        {
            if (await AdbAsync(command.Skip(2).ToArray(), TimeSpan.FromSeconds(15)) is { } failed)
            {
                FitProblem = failed;
                return false;
            }
        }

        await AdbAsync(["shell", PhoneShell.LetterboxApps], TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Once per session: the watchdog that puts the screen back if the mirror ends unannounced.
        if (!_fitMade)
        {
            await AdbAsync(["shell", PhoneShell.Watchdog], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        _fitMade = true;
        Fit = fit;
        FitProblem = null;
        return true;
    }

    /// <summary>Night, as the dash has it, as the phone's dark theme — told when it changes.</summary>
    public async Task NightAsync(bool dark)
    {
        if (State != PhoneState.Running || _nightTold == dark)
        {
            return;
        }

        if (await AdbAsync(["shell", PhoneShell.Night(dark)], TimeSpan.FromSeconds(15)) is { } failed)
        {
            NightProblem = failed;
            return;
        }

        _nightTold = dark;
        NightProblem = null;
    }

    /// <summary>One line for <c>--shot</c> and the hold menu.</summary>
    public string Describe() => $"phone · {State.ToString().ToLowerInvariant()} · {Detail}";

    /// <summary>Stop mirroring and put the phone back: its screen's own size and density, its display on.</summary>
    public async ValueTask DisposeAsync()
    {
        if (State is PhoneState.Stopped or PhoneState.Stopping)
        {
            return;
        }

        Set(PhoneState.Stopping, $"giving the {Phone.Name} its screen back");
        await SendAsync(ScrcpyControl.DisplayPower(true)).ConfigureAwait(false);
        await _life.CancelAsync().ConfigureAwait(false);
        await PutBackAsync().ConfigureAwait(false);
        Close();
        Set(PhoneState.Stopped, "stopped");
    }

    /// <summary>For the dash closing: the same, bounded, since the dash has ten seconds to close.</summary>
    public async Task LetGoAsync()
    {
        if (State is PhoneState.Stopped)
        {
            return;
        }

        await SendAsync(ScrcpyControl.DisplayPower(true)).ConfigureAwait(false);
        await _life.CancelAsync().ConfigureAwait(false);
        await PutBackAsync().WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        Close();
        Set(PhoneState.Stopped, "stopped");
    }

    private async Task PutBackAsync()
    {
        if (_fitMade)
        {
            await AdbAsync(["shell", PhoneShell.PutBack], TimeSpan.FromSeconds(10), ignoreLife: true)
                .ConfigureAwait(false);
        }

        if (_port > 0)
        {
            await AdbAsync(["forward", "--remove", string.Create(CultureInfo.InvariantCulture, $"tcp:{_port}")], TimeSpan.FromSeconds(5), ignoreLife: true)
                .ConfigureAwait(false);
        }
    }

    private void Close()
    {
        _videoSocket?.Dispose();
        _controlSocket?.Dispose();

        try
        {
            if (_server is { HasExited: false } server)
            {
                server.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        _server?.Dispose();
    }

    /// <summary>Run <c>adb -s serial …</c>, one at a time. Null when it was done; else why not.</summary>
    private async Task<string?> AdbAsync(IReadOnlyList<string> arguments, TimeSpan within, bool ignoreLife = false)
    {
        var ct = ignoreLife ? CancellationToken.None : _lifetime;

        try
        {
            await _adbGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "stopped";
        }

        try
        {
            var (done, output) = await Adb.RunAsync(_adb, ["-s", Phone.Serial, .. arguments], within, ct).ConfigureAwait(false);
            return done ? null : AdbAnswers.State(output);
        }
        finally
        {
            _adbGate.Release();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void Keep(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_serverOutput)
        {
            if (_serverOutput.Count < 500)
            {
                _serverOutput.Add(line);
            }
        }
    }

    /// <summary>What the server said when it failed, or null.</summary>
    private string? Said()
    {
        lock (_serverOutput)
        {
            return ScrcpyServer.Failure(_serverOutput);
        }
    }

    private void Set(PhoneState state, string detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }
}
