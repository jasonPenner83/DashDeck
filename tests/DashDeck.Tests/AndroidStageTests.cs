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

    private static IReadOnlyList<AvdInfo> List() => AndroidChoice.List(
        AvdHome,
        folder => Files.Keys.Where(k => Path.GetDirectoryName(k) == folder),
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
