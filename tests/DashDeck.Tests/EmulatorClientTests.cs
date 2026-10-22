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
    public async Task A_device_left_on_its_side_still_reports_its_own_screen()
    {
        using var client = new EmulatorClient(_port, Token);
        _fake.Turn = Rotation.Types.SkinRotation.Landscape;   // the picture comes 792 wide, 1408 tall

        Assert.Equal((1408, 792), await client.ScreenSizeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Frames_say_how_the_emulator_turned_them()
    {
        using var client = new EmulatorClient(_port, Token);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _fake.Turn = Rotation.Types.SkinRotation.ReverseLandscape;
        var turns = -1;

        await client.StreamFramesAsync(912, 636, frame =>
        {
            turns = frame.QuarterTurns;
            cancel.Cancel();
        }, cancel.Token);

        Assert.Equal(3, turns);
    }

    [Fact]
    public async Task Turning_the_device_is_a_rotation_about_the_screen_s_axis()
    {
        using var client = new EmulatorClient(_port, Token);

        await client.RotateAsync(90);

        Assert.Equal(PhysicalModelValue.Types.PhysicalType.Rotation, _fake.Physical!.Target);
        Assert.Equal([0f, 0f, 90f], _fake.Physical.Value.Data);
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

        Assert.Equal([(100, 200, 1), (110, 210, 1), (110, 210, 0)], await _fake.TouchesWhen(3));
        Assert.Equal(0, _fake.SingleTouchCalls);   // all on the one stream
    }

    [Fact]
    public async Task A_drag_arrives_in_the_order_it_was_made()
    {
        using var client = new EmulatorClient(_port, Token);
        var drag = Enumerable.Range(0, 200).Select(i => (X: i, Y: 2 * i, Pressure: i < 199 ? 1 : 0)).ToList();

        foreach (var (x, y, pressure) in drag)
        {
            await client.TouchAsync(x, y, down: pressure == 1);
        }

        Assert.Equal(drag, await _fake.TouchesWhen(drag.Count));
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
    public async Task The_emulator_says_how_it_draws_and_what_android_has()
    {
        using var client = new EmulatorClient(_port, Token);

        var machine = (await client.MachineAsync(CancellationToken.None))!;

        Assert.Equal("host", machine.Config["hw.gpu.mode"]);
        Assert.Equal("6 cores · 4 GB", machine.Describe());
        Assert.Equal("host", Core.Emulation.EmulatorGpu.FromConfig(machine.Config));

        using var refused = new EmulatorClient(_port, "wrong");
        Assert.Null(await refused.MachineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Back_and_home_are_pressed_by_the_names_android_studio_uses()
    {
        using var client = new EmulatorClient(_port, Token);

        Assert.True(await client.PressKeyAsync(Core.Emulation.AndroidKeys.Name(Core.Emulation.AndroidKey.Back)));
        Assert.True(await client.PressKeyAsync(Core.Emulation.AndroidKeys.Name(Core.Emulation.AndroidKey.Home)));

        Assert.Equal(["GoBack", "GoHome"], _fake.Keys);
        Assert.All(_fake.KeyTypes, t => Assert.Equal(KeyboardEvent.Types.KeyEventType.Keypress, t));
    }

    [Fact]
    public async Task The_microphone_is_let_through_and_shut_off_again()
    {
        using var client = new EmulatorClient(_port, Token);

        Assert.True(await client.SetMicrophoneAsync(true));
        Assert.True(_fake.Microphone);

        Assert.True(await client.SetMicrophoneAsync(false));
        Assert.False(_fake.Microphone);

        using var refused = new EmulatorClient(_port, "wrong");
        Assert.False(await refused.SetMicrophoneAsync(true));
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

    [Fact]
    public void Only_the_cropped_part_of_a_frame_becomes_the_bitmap()
    {
        // A 3 × 2 frame whose pixels are numbered; the crop is its middle column.
        byte[] rgb = [.. Enumerable.Range(0, 6).SelectMany(i => new[] { (byte)i, (byte)(i + 10), (byte)(i + 20) })];
        var bgrx = new byte[8];

        FramePixels.RgbToBgr32(rgb, 3, (1, 0, 1, 2), bgrx);

        Assert.Equal([21, 11, 1, 0, 24, 14, 4, 0], bgrx);
    }

    /// <summary>An emulator that answers as the real one does, and remembers what it was told.</summary>
    internal sealed class FakeEmulator : EmulatorController.EmulatorControllerBase
    {
        public int BootAfterCalls { get; set; }

        public int StatusCalls { get; private set; }

        public List<(int X, int Y, int Pressure)> Touches { get; } = [];

        public int SingleTouchCalls { get; private set; }

        public Rotation.Types.SkinRotation Turn { get; set; }

        public PhysicalModelValue? Physical { get; private set; }

        public bool? Microphone { get; private set; }

        public List<string> Keys { get; } = [];

        public List<KeyboardEvent.Types.KeyEventType> KeyTypes { get; } = [];

        public override Task<Empty> sendKey(KeyboardEvent request, ServerCallContext context)
        {
            Authorise(context);
            Keys.Add(request.Key);
            KeyTypes.Add(request.EventType);
            return Task.FromResult(new Empty());
        }

        /// <summary>The touches, once there are <paramref name="count"/> of them — they come on a stream, not at once.</summary>
        public async Task<List<(int X, int Y, int Pressure)>> TouchesWhen(int count)
        {
            for (var waited = 0; waited < 5000; waited += 10)
            {
                lock (Touches)
                {
                    if (Touches.Count >= count)
                    {
                        return [.. Touches];
                    }
                }

                await Task.Delay(10);
            }

            lock (Touches)
            {
                return [.. Touches];
            }
        }

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
            var status = new EmulatorStatus
            {
                Version = "fake",
                Booted = StatusCalls >= BootAfterCalls,
                VmConfig = new VmConfiguration { NumberOfCpuCores = 6, RamSizeBytes = 4L << 30, HypervisorType = VmConfiguration.Types.VmHypervisorType.Whpx },
            };
            status.PlatformConfig.Add("hw.gpu.mode", "host");
            status.PlatformConfig.Add("hw.gpu.enabled", "yes");
            return Task.FromResult(status);
        }

        public override Task<Image> getScreenshot(ImageFormat request, ServerCallContext context)
        {
            Authorise(context);
            var sideways = (int)Turn % 2 == 1;
            return Task.FromResult(new Image
            {
                Format = new ImageFormat
                {
                    Format = request.Format,
                    Width = sideways ? 792u : 1408u,
                    Height = sideways ? 1408u : 792u,
                    Rotation = new Rotation { Rotation_ = Turn },
                },
            });
        }

        public override async Task streamScreenshot(ImageFormat request, IServerStreamWriter<Image> responseStream, ServerCallContext context)
        {
            Authorise(context);
            var pixels = ByteString.CopyFrom(new byte[request.Width * request.Height * 3]);

            for (uint seq = 0; !context.CancellationToken.IsCancellationRequested; seq++)
            {
                await responseStream.WriteAsync(new Image
                {
                    Format = new ImageFormat { Format = request.Format, Width = request.Width, Height = request.Height, Rotation = new Rotation { Rotation_ = Turn } },
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
            SingleTouchCalls++;
            lock (Touches)
            {
                Touches.AddRange(request.Touches.Select(t => (t.X, t.Y, t.Pressure)));
            }

            return Task.FromResult(new Empty());
        }

        public override async Task<Empty> streamInputEvent(IAsyncStreamReader<InputEvent> requestStream, ServerCallContext context)
        {
            Authorise(context);

            try
            {
                await foreach (var input in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    lock (Touches)
                    {
                        Touches.AddRange(input.TouchEvent.Touches.Select(t => (t.X, t.Y, t.Pressure)));
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                // The client went; what it sent is kept.
            }

            return new Empty();
        }

        public override Task<Empty> setMicrophoneState(MicrophoneState request, ServerCallContext context)
        {
            Authorise(context);
            Microphone = request.RealAudioEnabled;
            return Task.FromResult(new Empty());
        }

        public override Task<Empty> setPhysicalModel(PhysicalModelValue request, ServerCallContext context)
        {
            Authorise(context);
            Physical = request;
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
