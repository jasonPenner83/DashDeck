using DashDeck.Core.Emulation;

namespace DashDeck.Tests;

/// <summary>The ANDROID nav cell and what it opens (ADR-0075).</summary>
public class AndroidStageTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_tap_opens_full_screen_and_a_double_tap_sends_it_to_the_stage()
    {
        var view = AndroidView.Default;
        Assert.True(view.IsFull);

        view = view.DoubleTapped();
        Assert.True(view.IsOnStage);
        Assert.True(view.DoubleTapped().IsOnStage);   // idempotent

        // A tap on the cell brings it back off the stage, full screen.
        Assert.True(view.Tapped().IsFull);
    }

    [Fact]
    public void A_quick_press_is_a_tap_and_a_second_soon_after_is_a_double_tap()
    {
        Assert.Equal(PressKind.Tap, PressGesture.Released(Ms(0), Ms(120), previousTap: null, holdFired: false));
        Assert.Equal(PressKind.DoubleTap, PressGesture.Released(Ms(300), Ms(380), previousTap: Ms(120), holdFired: false));
        Assert.Equal(PressKind.Tap, PressGesture.Released(Ms(900), Ms(980), previousTap: Ms(120), holdFired: false));
    }

    [Fact]
    public void A_long_press_is_a_hold_and_its_lift_means_nothing_once_the_menu_is_open()
    {
        Assert.Equal(PressKind.Hold, PressGesture.Released(Ms(0), Ms(700), previousTap: null, holdFired: false));
        Assert.Null(PressGesture.Released(Ms(0), Ms(900), previousTap: null, holdFired: true));

        // A hold right after a tap is still a hold, not a double tap.
        Assert.Equal(PressKind.Hold, PressGesture.Released(Ms(200), Ms(800), previousTap: Ms(100), holdFired: false));
    }

    private const string AvdHome = "/avd";

    private static readonly Dictionary<string, string> Files = new()
    {
        [Path.Combine(AvdHome, "Car.ini")] = "avd.ini.encoding=UTF-8\npath=/elsewhere/Car.avd\n",
        [Path.Combine("/elsewhere/Car.avd", "config.ini")] = "tag.id=android-automotive-playstore\nabi.type=x86_64\nhw.lcd.width=1408\nhw.lcd.height=792\n",
        [Path.Combine(AvdHome, "Tab.ini")] = "avd.ini.encoding=UTF-8\n",
        [Path.Combine(AvdHome, "Tab.avd", "config.ini")] = "tag.id=google_apis_playstore\nabi.type=x86_64\nhw.lcd.width=2560\nhw.lcd.height=1600\n",
        [Path.Combine(AvdHome, "Arm.ini")] = "path=/arm.avd\n",
        [Path.Combine("/arm.avd", "config.ini")] = "tag.id=google_apis\nabi.type=arm64-v8a\n",
        [Path.Combine(AvdHome, "Broken.ini")] = "path=/missing.avd\n",
    };

    // Folders compared as full paths: on Windows, Path.Combine("/avd", "Car.ini") is "/avd\Car.ini",
    // whose folder comes back as "\avd" — the same folder, spelled differently.
    private static IReadOnlyList<AvdInfo> List() => AndroidChoice.List(
        AvdHome,
        folder => Files.Keys.Where(k => Path.GetFullPath(Path.GetDirectoryName(k)!) == Path.GetFullPath(folder)),
        path => Files.GetValueOrDefault(path));

    [Fact]
    public void Avds_are_listed_from_the_emulator_s_own_files_without_running_anything()
    {
        var avds = List();

        Assert.Equal(["Arm", "Car", "Tab"], avds.Select(a => a.Name));   // Broken has no config, so it is left out
        Assert.Equal(AndroidFlavour.Automotive, avds.Single(a => a.Name == "Car").Flavour);
        Assert.Equal(2560, avds.Single(a => a.Name == "Tab").Width);
    }

    [Fact]
    public void Each_flavour_picks_its_own_avd_preferring_the_one_named_then_x86_64()
    {
        var avds = List();

        Assert.Equal("Car", AndroidChoice.Pick(avds, AndroidFlavour.Automotive)!.Name);
        Assert.Equal("Tab", AndroidChoice.Pick(avds, AndroidFlavour.Regular)!.Name);          // x86_64 before arm
        Assert.Equal("Arm", AndroidChoice.Pick(avds, AndroidFlavour.Regular, named: "Arm")!.Name);
        Assert.Equal("Tab", AndroidChoice.Pick(avds, AndroidFlavour.Regular, named: "Car")!.Name);   // named, but not that flavour
        Assert.Null(AndroidChoice.Pick([], AndroidFlavour.Automotive));
    }

    [Fact]
    public void Flavours_have_names_for_the_menu_and_for_the_settings_file()
    {
        Assert.Equal("ANDROID AUTOMOTIVE", AndroidChoice.Label(AndroidFlavour.Automotive));
        Assert.Equal("ANDROID", AndroidChoice.Label(AndroidFlavour.Regular));
        Assert.Equal(AndroidFlavour.Automotive, AndroidChoice.Parse("Automotive"));
        Assert.Equal(AndroidFlavour.Regular, AndroidChoice.Parse("nonsense"));
        Assert.Equal(AndroidFlavour.Regular, AndroidChoice.Parse(null));

        Assert.Equal("Car · 1408×792", AndroidChoice.Describe(List().Single(a => a.Name == "Car"), AndroidFlavour.Automotive));
        Assert.StartsWith("No Automotive AVD", AndroidChoice.Describe(null, AndroidFlavour.Automotive), StringComparison.Ordinal);
    }

    [Theory]
    // A 2560×1600 tablet turned upright: the picture is 1600 wide and 2560 tall. Its top-left corner,
    // a little in, is somewhere near one corner of the unturned screen — which one depends on the turn.
    [InlineData(0, 0.1, 0.2, 256, 320)]
    [InlineData(1, 0.1, 0.2, 2048, 160)]
    [InlineData(2, 0.1, 0.2, 2304, 1280)]
    [InlineData(3, 0.1, 0.2, 512, 1440)]
    public void A_touch_on_a_turned_picture_lands_on_the_unturned_screen(int turns, double x, double y, int deviceX, int deviceY) =>
        Assert.Equal((deviceX, deviceY), AndroidChoice.ToDevice(x, y, 2560, 1600, turns));

    [Fact]
    public void Turning_and_turning_back_lands_on_the_same_pixel()
    {
        // The middle is the middle whichever way up; a quarter turn and three quarters are mirror images.
        foreach (var turns in new[] { 0, 1, 2, 3 })
        {
            Assert.Equal((1280, 800), AndroidChoice.ToDevice(0.5, 0.5, 2560, 1600, turns));
        }

        var quarter = AndroidChoice.ToDevice(0.25, 0.75, 2560, 1600, 1)!.Value;
        var threeQuarters = AndroidChoice.ToDevice(0.75, 0.25, 2560, 1600, 3)!.Value;
        Assert.Equal(quarter, threeQuarters);
    }

    [Fact]
    public void A_regular_tablet_is_turned_to_the_shape_of_its_region_and_automotive_never_is()
    {
        // Full screen is tall (912 × 1170); the stage is wide (912 × 636).
        Assert.Equal(90, AndroidChoice.TurnFor(AndroidFlavour.Regular, 2560, 1600, 912, 1170));
        Assert.Equal(0, AndroidChoice.TurnFor(AndroidFlavour.Regular, 2560, 1600, 912, 636));

        // A phone is tall to begin with.
        Assert.Equal(0, AndroidChoice.TurnFor(AndroidFlavour.Regular, 1080, 2400, 912, 1170));
        Assert.Equal(90, AndroidChoice.TurnFor(AndroidFlavour.Regular, 1080, 2400, 912, 636));

        Assert.Null(AndroidChoice.TurnFor(AndroidFlavour.Automotive, 1408, 792, 912, 1170));
        Assert.Null(AndroidChoice.TurnFor(AndroidFlavour.Regular, 0, 0, 912, 1170));     // size not known yet
        Assert.Null(AndroidChoice.TurnFor(AndroidFlavour.Regular, 2560, 1600, 900, 910)); // square enough to leave
    }

    [Theory]
    [InlineData(16L << 30, 8, 4096, 4)]   // the owner's Surface Pro 7: half its eight threads (six was worse)
    [InlineData(8L << 30, 8, null, 4)]
    [InlineData(32L << 30, 16, 4096, 8)]
    [InlineData(16L << 30, 4, 4096, 2)]
    [InlineData(4L << 30, 2, null, null)]
    public void The_emulator_is_given_what_the_tablet_can_spare(long memory, int processors, int? memoryMb, int? cores) =>
        Assert.Equal((memoryMb, cores), EmulatorResources.For(memory, processors));

    [Fact]
    public void Each_flavour_has_its_own_keys_by_the_emulator_s_names()
    {
        Assert.Equal([AndroidKey.Back, AndroidKey.Home, AndroidKey.Recents], AndroidKeys.For(AndroidFlavour.Regular));
        Assert.Equal([AndroidKey.Back, AndroidKey.Home], AndroidKeys.For(AndroidFlavour.Automotive));
        Assert.Equal(["GoBack", "GoHome", "AppSwitch"], new[] { AndroidKey.Back, AndroidKey.Home, AndroidKey.Recents }.Select(AndroidKeys.Name));
    }

    [Fact]
    public void Dns_and_the_gpu_mode_go_on_the_command_line_only_when_given()
    {
        var arguments = EmulatorCommand.Arguments("Tab", 8554, dnsServers: ["192.168.1.1", "1.1.1.1"], gpu: "host");

        Assert.Equal(["-dns-server", "192.168.1.1,1.1.1.1", "-gpu", "host"], arguments.TakeLast(4));
        Assert.DoesNotContain("-dns-server", EmulatorCommand.Arguments("Tab", 8554));
        Assert.DoesNotContain("-gpu", EmulatorCommand.Arguments("Tab", 8554));

        Assert.Throws<ArgumentException>(() => EmulatorCommand.Arguments("Tab", 8554, dnsServers: ["not.an.address"]));
        Assert.Throws<ArgumentException>(() => EmulatorCommand.Arguments("Tab", 8554, gpu: "fast"));
    }

    [Fact]
    public void Memory_and_cores_go_on_the_command_line_only_when_given()
    {
        Assert.Equal(["-memory", "4096", "-cores", "4"], EmulatorCommand.Arguments("Tab", 8554, memoryMb: 4096, cores: 4).TakeLast(4));
        Assert.DoesNotContain("-memory", EmulatorCommand.Arguments("Tab", 8554));
    }

    [Fact]
    public void A_touch_on_the_picture_lands_on_the_device_s_own_pixels()
    {
        Assert.Equal((704, 396), AndroidChoice.ToDevice(0.5, 0.5, 1408, 792));
        Assert.Equal((0, 0), AndroidChoice.ToDevice(0, 0, 1408, 792));
        Assert.Equal((1407, 791), AndroidChoice.ToDevice(1, 1, 1408, 792));   // the far edge is the last pixel, not past it
        Assert.Null(AndroidChoice.ToDevice(1.2, 0.5, 1408, 792));            // beside a letterboxed picture
        Assert.Null(AndroidChoice.ToDevice(0.5, 0.5, 0, 0));                 // size not known yet
    }
}
