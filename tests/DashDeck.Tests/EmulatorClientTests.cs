using System.Net;
using Android.Emulation.Control;
using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Emulator;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DashDeck.Tests;

/// <summary>
/// The dash's gRPC client against a fake Android Emulator served in-process (ADR-0075) — the same
/// generated service definition on both sides, so the fake answers exactly what the real one is
/// asked.
/// </summary>
public sealed class EmulatorClientTests : IAsyncLifetime
{
    private const string Token = "the-token";
    private readonly FakeEmulator _fake = new();
    private WebApplication? _app;
    private int _port;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_fake);

        _app = builder.Build();
        _app.MapGrpcService<FakeEmulator>();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _port = new Uri(address).Port;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Boot_is_waited_for_with_the_token()
    {
        using var client = new EmulatorClient(_port, Token);
        _fake.BootAfterCalls = 3;

        Assert.True(await client.WaitForBootAsync(TimeSpan.FromSeconds(10), () => false, CancellationToken.None));
        Assert.True(_fake.StatusCalls >= 3);
    }

    [Fact]
    public async Task A_wrong_token_is_refused_at_once_rather_than_waited_out()
    {
        using var client = new EmulatorClient(_port, "wrong");

        await Assert.ThrowsAsync<EmulatorRefusedException>(() =>
            client.WaitForBootAsync(TimeSpan.FromMinutes(5), () => false, CancellationToken.None));
    }

    [Fact]
    public async Task Giving_up_stops_the_wait()
    {
        using var client = new EmulatorClient(_port, Token);
        _fake.BootAfterCalls = int.MaxValue;

        Assert.False(await client.WaitForBootAsync(TimeSpan.FromMinutes(5), () => true, CancellationToken.None));
    }

    [Fact]
    public async Task The_screen_size_is_the_device_s_own()
    {
        using var client = new EmulatorClient(_port, Token);

        Assert.Equal((1408, 792), await client.ScreenSizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Frames_arrive_at_the_size_asked_until_cancelled()
    {
        using var client = new EmulatorClient(_port, Token);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frames = new List<EmulatorFrame>();

        await client.StreamFramesAsync(912, 636, frame =>
        {
            frames.Add(frame);
            if (frames.Count == 3)
            {
                cancel.Cancel();
            }
        }, cancel.Token);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal((912, 636, 912 * 636 * 3), (f.Width, f.Height, f.Rgb.Length)));
        Assert.Equal([0u, 1u, 2u], frames.Select(f => f.Sequence));
    }

    [Fact]
    public async Task A_touch_is_a_pressure_at_a_point_and_a_lift_is_none()
    {
        using var client = new EmulatorClient(_port, Token);

        await client.TouchAsync(100, 200, down: true);
        await client.TouchAsync(110, 210, down: true);
        await client.TouchAsync(110, 210, down: false);

        Assert.Equal([(100, 200, 1), (110, 210, 1), (110, 210, 0)], _fake.Touches);
    }

    [Fact]
    public async Task The_truck_s_position_is_given_in_metres_a_second_and_never_without_a_fix()
    {
        using var client = new EmulatorClient(_port, Token);

        await client.SetPositionAsync(new LocationFix(10, 20, 36, 90, SignalQuality.Unavailable, DateTimeOffset.UnixEpoch));
        Assert.Null(_fake.Gps);

        await client.SetPositionAsync(new LocationFix(10, 20, 36, 90, SignalQuality.Live, DateTimeOffset.UnixEpoch));
        Assert.Equal((10.0, 20.0, 10.0, 90.0), (_fake.Gps!.Latitude, _fake.Gps.Longitude, _fake.Gps.Speed, _fake.Gps.Bearing));
    }

    [Fact]
    public async Task Shutting_down_asks_android_to_shut_down()
    {
        using var client = new EmulatorClient(_port, Token);

        Assert.True(await client.ShutdownAsync());
        Assert.Equal(VmRunState.Types.RunState.Shutdown, _fake.VmState);

        using var refused = new EmulatorClient(_port, "wrong");
        Assert.False(await refused.ShutdownAsync());
    }

    [Fact]
    public void Rgb_becomes_the_bitmap_s_blue_green_red()
    {
        byte[] rgb = [10, 20, 30, 40, 50, 60];
        var bgrx = new byte[8];

        FramePixels.RgbToBgr32(rgb, bgrx);

        Assert.Equal([30, 20, 10, 0, 60, 50, 40, 0], bgrx);
    }

    /// <summary>An emulator that answers as the real one does, and remembers what it was told.</summary>
    internal sealed class FakeEmulator : EmulatorController.EmulatorControllerBase
    {
        public int BootAfterCalls { get; set; }

        public int StatusCalls { get; private set; }

        public List<(int X, int Y, int Pressure)> Touches { get; } = [];

        public GpsState? Gps { get; private set; }

        public VmRunState.Types.RunState? VmState { get; private set; }

        private static void Authorise(ServerCallContext context)
        {
            if (context.RequestHeaders.GetValue("authorization") != "Bearer " + Token)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing the 'authorization' header with security credentials."));
            }
        }

        public override Task<EmulatorStatus> getStatus(Empty request, ServerCallContext context)
        {
            Authorise(context);
            StatusCalls++;
            return Task.FromResult(new EmulatorStatus { Version = "fake", Booted = StatusCalls >= BootAfterCalls });
        }

        public override Task<Image> getScreenshot(ImageFormat request, ServerCallContext context)
        {
            Authorise(context);
            return Task.FromResult(new Image { Format = new ImageFormat { Format = request.Format, Width = 1408, Height = 792 } });
        }

        public override async Task streamScreenshot(ImageFormat request, IServerStreamWriter<Image> responseStream, ServerCallContext context)
        {
            Authorise(context);
            var pixels = ByteString.CopyFrom(new byte[request.Width * request.Height * 3]);

            for (uint seq = 0; !context.CancellationToken.IsCancellationRequested; seq++)
            {
                await responseStream.WriteAsync(new Image
                {
                    Format = new ImageFormat { Format = request.Format, Width = request.Width, Height = request.Height },
                    Image_ = pixels,
                    Seq = seq,
                });

                try
                {
                    await Task.Delay(20, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public override Task<Empty> sendTouch(TouchEvent request, ServerCallContext context)
        {
            Authorise(context);
            lock (Touches)
            {
                Touches.AddRange(request.Touches.Select(t => (t.X, t.Y, t.Pressure)));
            }

            return Task.FromResult(new Empty());
        }

        public override Task<Empty> setGps(GpsState request, ServerCallContext context)
        {
            Authorise(context);
            Gps = request;
            return Task.FromResult(new Empty());
        }

        public override Task<Empty> setVmState(VmRunState request, ServerCallContext context)
        {
            Authorise(context);
            VmState = request.State;
            return Task.FromResult(new Empty());
        }
    }
}
