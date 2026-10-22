using Android.Emulation.Control;
using DashDeck.Core.Location;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace DashDeck.Emulator;

/// <summary>One frame of the emulated screen: RGB, three bytes a pixel, rows top to bottom.</summary>
public readonly record struct EmulatorFrame(byte[] Rgb, int Width, int Height, uint Sequence);

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

    public EmulatorClient(int port, string? token)
    {
        Port = port;
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new EmulatorController.EmulatorControllerClient(_channel);
        _headers = token is null ? new Metadata() : new Metadata { { "authorization", "Bearer " + token } };
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
        return ((int)image.Format.Width, (int)image.Format.Height);
    }

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
                    onFrame(new EmulatorFrame(image.Image_.ToByteArray(), (int)image.Format.Width, (int)image.Format.Height, image.Seq));
                }
            }
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>A finger down, moved or lifted, in the device's own pixels.</summary>
    public async Task TouchAsync(int x, int y, bool down, int finger = 1)
    {
        var touch = new TouchEvent();
        touch.Touches.Add(new Touch { X = x, Y = y, Identifier = finger, Pressure = down ? 1 : 0 });

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

    public void Dispose() => _channel.Dispose();
}

/// <summary>Turning the emulator's RGB into what a WPF bitmap holds.</summary>
public static class FramePixels
{
    /// <summary>
    /// RGB, three bytes a pixel, into BGRX, four — the layout of a <c>Bgr32</c> bitmap. The fourth
    /// byte is left alone, since Bgr32 ignores it.
    /// </summary>
    public static void RgbToBgr32(ReadOnlySpan<byte> rgb, Span<byte> bgrx)
    {
        var pixels = Math.Min(rgb.Length / 3, bgrx.Length / 4);

        for (int i = 0, from = 0, to = 0; i < pixels; i++, from += 3, to += 4)
        {
            bgrx[to] = rgb[from + 2];
            bgrx[to + 1] = rgb[from + 1];
            bgrx[to + 2] = rgb[from];
        }
    }
}
