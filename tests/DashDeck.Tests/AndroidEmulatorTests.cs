using DashDeck.Core.Emulation;

namespace DashDeck.Tests;

/// <summary>Finding and describing an Android emulator for the stage (ADR-0075).</summary>
public class AndroidEmulatorTests
{
    [Fact]
    public void An_automotive_image_is_told_from_a_regular_one_by_its_tag()
    {
        var car = AvdInfo.Parse("Car", """
            avd.ini.encoding=UTF-8
            abi.type=x86_64
            tag.id=android-automotive-playstore
            hw.lcd.width=1408
            hw.lcd.height=792
            hw.lcd.density=160
            hw.ramSize=4096M
            """);

        var tablet = AvdInfo.Parse("Tablet", """
            abi.type = x86_64
            tag.id = google_apis_playstore
            PlayStore.enabled = true
            hw.lcd.width = 2560
            hw.lcd.height = 1600
            hw.ramSize = 4G
            # a comment
            not a key
            """);

        Assert.Equal(AndroidFlavour.Automotive, car.Flavour);
        Assert.Equal((1408, 792, 160, 4096, true), (car.Width, car.Height, car.Density, car.RamMb, car.PlayStore));

        Assert.Equal(AndroidFlavour.Regular, tablet.Flavour);
        Assert.Equal((2560, 1600, 4096, true), (tablet.Width, tablet.Height, tablet.RamMb, tablet.PlayStore));
        Assert.Equal("Tablet — regular, x86_64, 2560×1600, 4096 MB, Play [google_apis_playstore]", tablet.Describe());
    }

    [Fact]
    public void A_config_that_says_little_is_still_read()
    {
        var bare = AvdInfo.Parse("Bare", "");

        Assert.Equal(AndroidFlavour.Regular, bare.Flavour);
        Assert.Equal((0, 0, false), (bare.Width, bare.RamMb, bare.PlayStore));
        Assert.Contains("screen ?", bare.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sdk_is_looked_for_where_it_is_named_then_where_the_sdk_and_android_studio_put_it()
    {
        var environment = new Dictionary<string, string?> { ["ANDROID_HOME"] = "D:/sdk", ["ANDROID_SDK_ROOT"] = "D:/sdk" };
        var candidates = AndroidSdk.Candidates("E:/mine", k => environment.GetValueOrDefault(k), "C:/Users/me/AppData/Local");

        Assert.Equal(["E:/mine", "D:/sdk", Path.Combine("C:/Users/me/AppData/Local", "Android", "Sdk")], candidates);

        var found = AndroidSdk.Find(candidates, path => path == AndroidSdk.EmulatorPath("D:/sdk"));
        Assert.Equal("D:/sdk", found);
        Assert.Null(AndroidSdk.Find(candidates, _ => false));
    }

    [Fact]
    public void Avds_live_where_the_emulator_looks_for_them()
    {
        Assert.Equal("X:/avds", AndroidSdk.AvdHome(k => k == "ANDROID_AVD_HOME" ? "X:/avds" : null, "C:/Users/me"));
        Assert.Equal(Path.Combine("Y:/home", "avd"), AndroidSdk.AvdHome(k => k == "ANDROID_USER_HOME" ? "Y:/home" : null, "C:/Users/me"));
        Assert.Equal(Path.Combine("C:/Users/me", ".android", "avd"), AndroidSdk.AvdHome(_ => null, "C:/Users/me"));
    }

    [Fact]
    public void The_emulator_is_started_hidden_with_a_token_on_its_grpc_port()
    {
        Assert.Equal(
            ["-avd", "Car", "-qt-hide-window", "-grpc", "8554", "-grpc-use-token", "-no-boot-anim"],
            EmulatorCommand.Arguments("Car", 8554));

        Assert.Contains("-no-window", EmulatorCommand.Arguments("Car", 8554, noWindow: true));
        Assert.Equal(["-skin", "1824x1272"], EmulatorCommand.Arguments("Tab", 8554, skin: "1824x1272").TakeLast(2));
        Assert.Throws<ArgumentException>(() => EmulatorCommand.Arguments("Tab", 8554, skin: "big"));
        Assert.False(EmulatorCommand.IsSize("100x100"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmulatorCommand.Arguments("Car", 80));
        Assert.Throws<ArgumentException>(() => EmulatorCommand.Arguments(" ", 8554));
    }

    [Fact]
    public void A_discovery_file_gives_the_port_and_token_and_is_printed_with_secrets_hidden()
    {
        var discovery = EmulatorDiscovery.Parse("""
            port.serial=5554
            port.adb=5555
            avd.name=Car
            grpc.port=8554
            grpc.token=abcdef123456
            grpc.jwks=C:\Temp\avd\running\1234\jwks
            """);

        Assert.Equal(8554, discovery.GrpcPort);
        Assert.Equal("abcdef123456", discovery.GrpcToken);
        Assert.Equal("Car", discovery.AvdName);
        Assert.Equal("emulator-5554", discovery.AdbSerial);

        var lines = discovery.Redacted().ToList();
        Assert.Contains("grpc.token=<12 characters, hidden>", lines);
        Assert.Contains(lines, l => l.StartsWith("grpc.jwks=<", StringComparison.Ordinal));
        Assert.Contains("port.adb=5555", lines);
        Assert.DoesNotContain(lines, l => l.Contains("abcdef", StringComparison.Ordinal));
    }

    [Fact]
    public void A_discovery_file_without_grpc_says_so()
    {
        var discovery = EmulatorDiscovery.Parse("port.serial=5554\ngrpc.token=\n");

        Assert.Null(discovery.GrpcPort);
        Assert.Null(discovery.GrpcToken);
        Assert.Equal("emulator-5554", discovery.AdbSerial);
        Assert.Null(EmulatorDiscovery.Parse("grpc.port=8554").AdbSerial);
        Assert.Equal("pid_1234.ini", EmulatorDiscovery.FileName(1234));
    }

    [Fact]
    public void The_discovery_file_is_where_the_emulator_says_it_wrote_it()
    {
        string[] output =
        [
            "INFO         | Android emulator version 37.2.12.0",
            @"INFO         | Advertising in: C:\Users\me\AppData\Local\Temp\avd\running\pid_4321.ini",
            "INFO         | Boot completed",
        ];

        Assert.Equal(@"C:\Users\me\AppData\Local\Temp\avd\running\pid_4321.ini", EmulatorDiscovery.AdvertisedPath(output));
        Assert.Null(EmulatorDiscovery.AdvertisedPath(["INFO | Advertising in: nowhere"]));
        Assert.Null(EmulatorDiscovery.AdvertisedPath([]));
    }

    [Fact]
    public void Without_being_told_the_newest_discovery_file_since_the_start_is_taken()
    {
        var start = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        var newest = EmulatorDiscovery.Newest(
            [
                ("run/pid_100.ini", start.AddMinutes(-30)),   // a crashed emulator from before
                ("run/pid_200.ini", start.AddSeconds(4)),
                ("run/pid_300.ini", start.AddSeconds(9)),
                ("run/notes.txt", start.AddSeconds(20)),
            ],
            start);

        Assert.Equal("run/pid_300.ini", newest);
        Assert.Null(EmulatorDiscovery.Newest([("run/pid_100.ini", start.AddMinutes(-30))], start));
    }

    [Theory]
    [InlineData("C:/Temp/avd/running/pid_8280.ini", 8280)]
    [InlineData("pid_10488.INI", 10488)]
    [InlineData("pid_.ini", null)]
    [InlineData("pid_12x.ini", null)]
    [InlineData("pid_123.txt", null)]
    [InlineData("other_123.ini", null)]
    public void A_discovery_file_names_the_process_it_is_for(string path, int? pid) =>
        Assert.Equal(pid, EmulatorDiscovery.PidOf(path));

    [Fact]
    public void Discovery_files_are_looked_for_in_the_runtime_then_temp_then_profile_folders()
    {
        var folders = EmulatorDiscovery.Folders(k => k == "XDG_RUNTIME_DIR" ? "/run/user/1" : null, "/tmp", "/home/me");

        Assert.Equal(
            [Path.Combine("/run/user/1", "avd", "running"), Path.Combine("/tmp", "avd", "running"), Path.Combine("/home/me", ".android", "avd", "running")],
            folders);
        Assert.Equal(2, EmulatorDiscovery.Folders(_ => null, "/tmp", "/home/me").Count);
    }

    [Fact]
    public void Settings_counts_as_opened_only_when_android_says_so()
    {
        Assert.True(AdbAnswers.Started("Starting: Intent { act=android.settings.SETTINGS }"));
        Assert.True(AdbAnswers.Started("Starting: Intent { act=android.settings.SETTINGS }\nWarning: Activity not started, intent has been delivered to currently running top-most instance."));

        // How it read as success on the Surface: ADB's own failure, without the word "error".
        Assert.False(AdbAnswers.Started("adb.exe: device offline"));
        Assert.False(AdbAnswers.Started("Starting: Intent { act=x }\nError: Activity not started, unable to resolve Intent"));
        Assert.False(AdbAnswers.Started(""));
    }

    [Fact]
    public void Adb_states_are_read_plainly()
    {
        Assert.Equal("device", AdbAnswers.State("device\r\n"));
        Assert.Equal("device offline", AdbAnswers.State("adb.exe: device offline"));
        Assert.Equal("device 'emulator-5554' not found", AdbAnswers.State("error: device 'emulator-5554' not found"));
        Assert.Equal("no answer", AdbAnswers.State(""));
    }

    [Fact]
    public void Two_adb_programs_in_the_emulator_s_output_are_both_named()
    {
        string[] output =
        [
            @"WARNING | adb command 'C:\Sdk\platform-tools\adb.exe -s emulator-5554 shell getprop sys.boot_completed ' failed: 'adb.exe: device offline'",
            @"WARNING | adb command 'C:\scrcpy\adb.exe -s emulator-5554 shell getprop sys.boot_completed ' failed: 'adb.exe: device offline'",
            @"WARNING | adb command 'C:\Sdk\platform-tools\adb.exe -s emulator-5554 shell ps' failed: 'adb.exe: device offline'",
            "INFO | something else",
        ];

        Assert.Equal([@"C:\Sdk\platform-tools\adb.exe", @"C:\scrcpy\adb.exe"], AdbAnswers.Programs(output));
    }

    [Fact]
    public void Frames_are_counted_as_a_rate_with_the_longest_stutter()
    {
        var meter = new FrameMeter();
        Assert.Equal(0, meter.FramesPerSecond);

        for (var i = 0; i <= 30; i++)
        {
            meter.Add(TimeSpan.FromMilliseconds(i == 30 ? 1100 : i * 33.3), 1_048_576);
        }

        Assert.Equal(31, meter.Count);
        Assert.Equal(30 / 1.1, meter.FramesPerSecond, 3);
        Assert.Equal(30 / 1.1, meter.MegabytesPerSecond, 3);
        Assert.Equal(1100 - (29 * 33.3), meter.LongestGap.TotalMilliseconds, 3);
    }
}
