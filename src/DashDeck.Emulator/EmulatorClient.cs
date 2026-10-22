using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Channels;
using Android.Emulation.Control;
using DashDeck.Core.Location;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace DashDeck.Emulator;

/// <summary>
/// One frame of the emulated screen: RGB, three bytes a pixel, rows top to bottom. The pixels are the
/// gRPC message's own, not a copy — a frame is megabytes, and a copy of each was garbage for the
/// dash's collector to sweep several times a second.
/// </summary>
/// <param name="QuarterTurns">
/// How the emulator turned the picture upright (its <c>SkinRotation</c>, 0–3) — what a touch on it
/// has to be turned back by (<c>AndroidChoice.ToDevice</c>).
/// </param>
public readonly record struct EmulatorFrame(ReadOnlyMemory<byte> Rgb, int Width, int Height, uint Sequence, int QuarterTurns = 0);

/// <summary>What the running emulator says it is (ADR-0075 addendum).</summary>
/// <param name="Config">Its hardware settings as it resolved them — <c>hw.gpu.mode</c> and the rest.</param>
/// <param name="Cores">The processor cores Android has.</param>
/// <param name="RamBytes">The memory Android has.</param>
/// <param name="Hypervisor">What runs it: <c>Whpx</c> on Windows.</param>
public sealed record EmulatorMachine(IReadOnlyDictionary<string, string> Config, int Cores, long RamBytes, string Hypervisor)
{
    /// <summary>One line for the hold menu: <c>6 cores · 4 GB</c>.</summary>
    public string Describe() => Cores > 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Cores} cores · {RamBytes / (1024.0 * 1024 * 1024):0.#} GB")
        : "";
}

/// <summary>The emulator refused a call: the token was missing or wrong (ADR-0075).</summary>
public sealed class EmulatorRefusedException(string message) : Exception(message);

/// <summary>
/// Talking to one running Android Emulator over its gRPC interface (ADR-0075).
/// </summary>
/// <remarks>
/// Localhost only, and every call carries the token the emulator wrote into its discovery file — so
/// nothing else on the tablet that finds the port can see the screen or type into it. The calls are
/// the emulator's own (<c>emulator_controller.proto</c>); this only names the few the stage needs.
/// </remarks>
public sealed class EmulatorClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly EmulatorController.EmulatorControllerClient _client;
    private readonly Metadata _headers;

    // Touch goes through one ordered stream rather than a call each: separate calls can overtake one
    // another, and a drag whose moves arrive out of order is a drag that lags and jumps — which is
    // how it felt on the Surface.
    private readonly Channel<TouchEvent> _touches = Channel.CreateUnbounded<TouchEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationToken _closed;
    private Task? _touchPump;

    public EmulatorClient(int port, string? token)
    {
        Port = port;
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new EmulatorController.EmulatorControllerClient(_channel);
        _headers = token is null ? new Metadata() : new Metadata { { "authorization", "Bearer " + token } };
        _closed = _closing.Token;
    }

    public int Port { get; }

    /// <summary>
    /// Wait until Android says it has booted. False when it has not within the time, or
    /// <paramref name="gaveUp"/> says the emulator is gone; throws when the token is refused, because
    /// waiting longer will not change that.
    /// </summary>
    public async Task<bool> WaitForBootAsync(TimeSpan within, Func<bool> gaveUp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gaveUp);

        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline && !gaveUp())
        {
            try
            {
                var status = await _client.getStatusAsync(new Empty(), _headers, DateTime.UtcNow.AddSeconds(5), ct).ConfigureAwait(false);
                if (status.Booted)
                {
                    return true;
                }
            }
            catch (RpcException e) when (e.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
            {
                throw new EmulatorRefusedException($"the emulator refused the token ({e.Status.Detail})");
            }
            catch (RpcException)
            {
                // Not listening yet, or busy booting; ask again.
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// The device's own screen size — what touch is given in. Asked as a full-size frame once,
    /// because the emulator's status does not carry it (seen on the Surface).
    /// </summary>
    public async Task<(int Width, int Height)> ScreenSizeAsync(CancellationToken ct)
    {
        var image = await _client.getScreenshotAsync(
            new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgb888 }, _headers, cancellationToken: ct).ConfigureAwait(false);

        // The picture comes turned upright; the screen touch is given in does not turn. A device that
        // was left on its side — the snapshot keeps that — would otherwise report its sides swapped.
        return QuarterTurns(image.Format) % 2 == 1
            ? ((int)image.Format.Height, (int)image.Format.Width)
            : ((int)image.Format.Width, (int)image.Format.Height);
    }

    /// <summary>
    /// Turn the device about its screen's axis, as Android Studio's rotate buttons do: 0 is its own
    /// way up, 90 a quarter turn. Android follows if its auto-rotate is on; the frames say how they came.
    /// </summary>
    public async Task RotateAsync(int degrees)
    {
        var value = new ParameterValue();
        value.Data.Add([0f, 0f, degrees]);

        try
        {
            await _client.setPhysicalModelAsync(
                new PhysicalModelValue { Target = PhysicalModelValue.Types.PhysicalType.Rotation, Value = value }, _headers).ConfigureAwait(false);
        }
        catch (RpcException)
        {
        }
    }

    private static int QuarterTurns(ImageFormat format) => (int)(format.Rotation?.Rotation_ ?? Rotation.Types.SkinRotation.Portrait);

    /// <summary>
    /// Frames as Android draws them, scaled to fit <paramref name="width"/>×<paramref name="height"/>,
    /// until cancelled. The emulator sends one only when the picture changes, so a still screen sends
    /// almost nothing — which is the point.
    /// </summary>
    public async Task StreamFramesAsync(int width, int height, Action<EmulatorFrame> onFrame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        using var call = _client.streamScreenshot(
            new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgb888, Width = (uint)width, Height = (uint)height },
            _headers, cancellationToken: ct);

        try
        {
            await foreach (var image in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (image.Image_.Length > 0 && image.Format.Width > 0 && image.Format.Height > 0)
                {
                    onFrame(new EmulatorFrame(image.Image_.Memory, (int)image.Format.Width, (int)image.Format.Height, image.Seq, QuarterTurns(image.Format)));
                }
            }
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>A finger down, moved or lifted, in the device's own pixels — queued, sent in order.</summary>
    public Task TouchAsync(int x, int y, bool down, int finger = 1)
    {
        var touch = new TouchEvent();
        touch.Touches.Add(new Touch { X = x, Y = y, Identifier = finger, Pressure = down ? 1 : 0 });

        _touches.Writer.TryWrite(touch);
        _touchPump ??= Task.Run(PumpTouchesAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// One <c>streamInputEvent</c> call carrying every touch in order. If the stream breaks, the touch
    /// in hand goes by itself and a new stream is opened for the next.
    /// </summary>
    private async Task PumpTouchesAsync()
    {
        var reader = _touches.Reader;

        while (!_closed.IsCancellationRequested)
        {
            AsyncClientStreamingCall<InputEvent, Empty>? call = null;

            try
            {
                call = _client.streamInputEvent(_headers, cancellationToken: _closed);

                while (await reader.WaitToReadAsync(_closed).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var touch))
                    {
                        try
                        {
                            await call.RequestStream.WriteAsync(new InputEvent { TouchEvent = touch }).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is RpcException or InvalidOperationException)
                        {
                            await SendAloneAsync(touch).ConfigureAwait(false);
                            throw;
                        }
                    }
                }

                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is RpcException or InvalidOperationException)
            {
                // Reopen on the next touch.
                if (await Task.Delay(100, _closed).ContinueWith(t => t.IsCanceled, TaskScheduler.Default).ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                call?.Dispose();
            }
        }
    }

    private async Task SendAloneAsync(TouchEvent touch)
    {
        try
        {
            await _client.sendTouchAsync(touch, _headers).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // One lost touch is not worth stopping for; the next one will tell.
        }
    }

    /// <summary>Where Android thinks it is: the truck's own fix (ADR-0075). Never sent anywhere else.</summary>
    public async Task SetPositionAsync(LocationFix fix)
    {
        if (!fix.HasFix)
        {
            return;
        }

        try
        {
            await _client.setGpsAsync(new GpsState
            {
                Latitude = fix.Latitude,
                Longitude = fix.Longitude,
                Speed = double.IsFinite(fix.GroundSpeedKmh) ? fix.GroundSpeedKmh / 3.6 : 0,
                Bearing = double.IsFinite(fix.CourseDegrees) ? fix.CourseDegrees : 0,
                Satellites = 10,
            }, _headers).ConfigureAwait(false);
        }
        catch (RpcException)
        {
        }
    }

    /// <summary>
    /// The running emulator's own configuration: its hardware settings as it resolved them (the GPU
    /// mode among them), and the processor cores and memory Android really has. Null when it would
    /// not say.
    /// </summary>
    public async Task<EmulatorMachine?> MachineAsync(CancellationToken ct)
    {
        try
        {
            var status = await _client.getStatusAsync(new Empty(), _headers, DateTime.UtcNow.AddSeconds(5), ct).ConfigureAwait(false);

            var config = new Dictionary<string, string>(status.PlatformConfig);

            // The older list, kept by the emulator beside the map that replaced it: read only for what
            // the map does not have.
#pragma warning disable CS0612
            foreach (var entry in status.HardwareConfig?.Entry ?? [])
#pragma warning restore CS0612
            {
                config.TryAdd(entry.Key, entry.Value);
            }

            return new EmulatorMachine(
                config,
                status.VmConfig?.NumberOfCpuCores ?? 0,
                status.VmConfig?.RamSizeBytes ?? 0,
                status.VmConfig?.HypervisorType.ToString() ?? "");
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>
    /// Let Android hear the tablet's microphone, or give it silence — the emulator's own default.
    /// What Android Studio's "virtual microphone uses host audio input" switch does. False when the
    /// emulator would not.
    /// </summary>
    public async Task<bool> SetMicrophoneAsync(bool hear)
    {
        try
        {
            await _client.setMicrophoneStateAsync(new MicrophoneState { RealAudioEnabled = hear }, _headers).ConfigureAwait(false);
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shut Android down as closing the emulator does, so its quick-boot snapshot is saved. True when
    /// it was asked; whether it went is the launcher's to see.
    /// </summary>
    public async Task<bool> ShutdownAsync()
    {
        try
        {
            await _client.setVmStateAsync(new VmRunState { State = VmRunState.Types.RunState.Shutdown }, _headers, DateTime.UtcNow.AddSeconds(10)).ConfigureAwait(false);
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _touches.Writer.TryComplete();
        _closing.Cancel();
        _closing.Dispose();
        _channel.Dispose();
    }
}

/// <summary>Turning the emulator's RGB into what a WPF bitmap holds.</summary>
public static class FramePixels
{
    // Four pixels at a time: twelve bytes of RGB into sixteen of BGRX, the fourth of each zero.
    private static readonly Vector128<byte> FourPixels = Vector128.Create((byte)2, 1, 0, 255, 5, 4, 3, 255, 8, 7, 6, 255, 11, 10, 9, 255);

    /// <summary>
    /// RGB, three bytes a pixel, into BGRX, four — the layout of a <c>Bgr32</c> bitmap, whose fourth
    /// byte is ignored. Four pixels to an instruction where the processor can, which on the
    /// Surface's frames is most of the work of drawing one.
    /// </summary>
    public static void RgbToBgr32(ReadOnlySpan<byte> rgb, Span<byte> bgrx)
    {
        var pixels = Math.Min(rgb.Length / 3, bgrx.Length / 4);
        var i = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            ref var source = ref MemoryMarshal.GetReference(rgb);
            ref var target = ref MemoryMarshal.GetReference(bgrx);

            // Each step reads sixteen bytes for twelve, so it stops while sixteen are still there.
            for (; i + 4 <= pixels && (i * 3) + 16 <= rgb.Length; i += 4)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(i * 3)), FourPixels)
                    .StoreUnsafe(ref target, (nuint)(i * 4));
            }
        }

        for (int from = i * 3, to = i * 4; i < pixels; i++, from += 3, to += 4)
        {
            bgrx[to] = rgb[from + 2];
            bgrx[to + 1] = rgb[from + 1];
            bgrx[to + 2] = rgb[from];
        }
    }

    /// <summary>
    /// Only part of the frame — the picture Android drew in the middle of its screen
    /// (<c>AndroidFit.Crop</c>) — into a bitmap that part's size.
    /// </summary>
    public static void RgbToBgr32(ReadOnlySpan<byte> rgb, int frameWidth, (int X, int Y, int Width, int Height) crop, Span<byte> bgrx)
    {
        if (frameWidth <= 0 || crop.Width <= 0 || crop.Height <= 0)
        {
            return;
        }

        for (var row = 0; row < crop.Height; row++)
        {
            var from = ((crop.Y + row) * frameWidth + crop.X) * 3;
            var to = row * crop.Width * 4;

            if (from + (crop.Width * 3) > rgb.Length || to + (crop.Width * 4) > bgrx.Length)
            {
                return;
            }

            RgbToBgr32(rgb.Slice(from, crop.Width * 3), bgrx.Slice(to, crop.Width * 4));
        }
    }
}
