using DashDeck.Core.Phone;

namespace DashDeck.Tests;

/// <summary>
/// The phone over ADB (ADR-0076): what ADB says, going wireless, finding the phone again, and the
/// watchdog that gives the phone its screen back.
/// </summary>
public class PhoneShellTests
{
    [Fact]
    public void A_shell_command_is_done_only_when_it_exits_cleanly_and_nothing_failed()
    {
        Assert.True(AdbAnswers.Done(0, ""));
        Assert.True(AdbAnswers.Done(0, "* daemon not running; starting now at tcp:5037\n* daemon started successfully"));
        Assert.True(AdbAnswers.Done(0, "scrcpy-server: 1 file pushed, 0 skipped."));

        Assert.False(AdbAnswers.Done(1, ""));
        Assert.False(AdbAnswers.Done(0, "adb.exe: device offline"));
        Assert.False(AdbAnswers.Done(0, "error: device 'HT6B0123456' not found"));
        Assert.False(AdbAnswers.Done(0, "java.lang.IllegalArgumentException: bad size"));
    }

    [Fact]
    public void Why_adb_failed_is_read_plainly()
    {
        Assert.Equal("device offline", AdbAnswers.State("adb.exe: device offline"));
        Assert.Equal("device 'HT6B0123456' not found", AdbAnswers.State("error: device 'HT6B0123456' not found"));
        Assert.Equal("no answer", AdbAnswers.State(""));
    }

    [Fact]
    public void The_phone_s_wifi_and_hotspot_addresses_are_found_and_nothing_else()
    {
        const string output = """
            1: lo    inet 127.0.0.1/8 scope host lo\       valid_lft forever preferred_lft forever
            12: rmnet_data0    inet 10.120.33.7/30 scope global rmnet_data0\       valid_lft forever preferred_lft forever
            31: wlan1    inet 10.71.140.1/24 brd 10.71.140.255 scope global wlan1\       valid_lft forever preferred_lft forever
            30: wlan0    inet 192.168.1.23/24 brd 192.168.1.255 scope global wlan0\       valid_lft forever preferred_lft forever
            40: p2p0    inet 192.168.49.1/24 scope global p2p0
            """;

        // Its Wi-Fi first, then its own hotspot; never mobile data, loopback or Wi-Fi Direct.
        Assert.Equal(["192.168.1.23", "10.71.140.1"], PhoneShell.Addresses(output));
        Assert.Empty(PhoneShell.Addresses("1: lo    inet 127.0.0.1/8 scope host lo"));
        Assert.Empty(PhoneShell.Addresses(null));
    }

    [Fact]
    public void The_phone_is_looked_for_where_it_was_then_at_the_tablet_s_gateway()
    {
        // On the phone's hotspot, the tablet's gateway is the phone.
        Assert.Equal(
            ["192.168.1.23:5555", "10.71.140.1:5555"],
            PhoneShell.Candidates("192.168.1.23:5555", ["10.71.140.1", "fe80::1", "0.0.0.0"]));

        Assert.Equal(["10.71.140.1:5555"], PhoneShell.Candidates(null, ["10.71.140.1"]));
        Assert.Equal(["192.168.1.23:5555"], PhoneShell.Candidates("192.168.1.23:5555", ["192.168.1.23"]));   // the same, once
    }

    [Fact]
    public void Adb_connect_is_read_for_what_it_did_not_its_exit_code()
    {
        Assert.True(PhoneShell.Connected("connected to 192.168.1.23:5555"));
        Assert.True(PhoneShell.Connected("already connected to 192.168.1.23:5555"));
        Assert.False(PhoneShell.Connected("failed to connect to '10.71.140.1:5555': Connection refused"));
        Assert.False(PhoneShell.Connected("cannot connect to 10.71.140.1:5555: A connection attempt failed"));
        Assert.False(PhoneShell.Connected(""));
    }

    [Fact]
    public void A_phone_by_cable_is_preferred_to_the_same_phone_over_wifi()
    {
        var devices = AdbDevice.Parse("""
            192.168.1.23:5555      device product:shiba model:Pixel_8 device:shiba transport_id:4
            35141FDH2000AB         device usb:1-1 product:shiba model:Pixel_8 device:shiba transport_id:3
            """);

        Assert.Equal("35141FDH2000AB", AdbDevice.Pick(devices)!.Serial);
        Assert.True(devices[0].IsWireless);
        Assert.False(devices[1].IsWireless);
        Assert.True(new AdbDevice("adb-35141FDH2000AB-a1b2c3._adb-tls-connect._tcp", "device", "Pixel_8").IsWireless);

        Assert.Equal("Pixel 8 · connected over Wi-Fi", AdbDevice.Describe(devices[0]));
        Assert.Equal("Pixel 8 · connected by USB", AdbDevice.Describe(devices[1]));
    }

    [Fact]
    public void The_watchdog_waits_for_scrcpy_without_seeing_itself_and_then_gives_the_screen_back()
    {
        // Bracketed, so the pattern cannot match the watchdog's own command line, which holds it.
        Assert.Contains("pgrep -f \"[c]om.genymobile.scrcpy.Server\"", PhoneShell.Watchdog, StringComparison.Ordinal);
        Assert.DoesNotContain("com.genymobile.scrcpy.Server", PhoneShell.Watchdog.Replace("[c]om", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // Detached and holding nothing open, so it outlives the connection that started it.
        Assert.StartsWith("setsid ", PhoneShell.Watchdog, StringComparison.Ordinal);
        Assert.EndsWith("</dev/null >/dev/null 2>&1 &", PhoneShell.Watchdog, StringComparison.Ordinal);
        Assert.Contains(PhoneShell.PutBack, PhoneShell.Watchdog, StringComparison.Ordinal);

        Assert.Equal("cmd uimode night yes", PhoneShell.Night(true));
    }

    [Fact]
    public void A_rate_counts_only_the_last_window()
    {
        var rate = new RollingRate(TimeSpan.FromSeconds(2));
        for (var ms = 0; ms < 3000; ms += 50)
        {
            rate.Add(TimeSpan.FromMilliseconds(ms));   // twenty a second, for three seconds
        }

        Assert.Equal(20, rate.PerSecond(TimeSpan.FromMilliseconds(2950)), 0);
        Assert.Equal(0, rate.PerSecond(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void The_busiest_second_is_remembered_after_the_screen_stops()
    {
        var rate = new RollingRate(TimeSpan.FromSeconds(15));

        // A second's scroll at 24 frames, then stillness — the menu is opened five seconds later.
        for (var i = 0; i < 24; i++)
        {
            rate.Add(TimeSpan.FromMilliseconds(1000 + (i * 40)));
        }

        Assert.Equal(24, rate.BusiestSecond(TimeSpan.FromSeconds(7)));
        Assert.Equal(0, rate.BusiestSecond(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void The_picture_size_goes_round_and_sets_the_long_side()
    {
        Assert.Equal("BALANCED", AndroidPicture.Parse(null).Name);
        Assert.Equal("FAST", AndroidPicture.Parse("fast").Name);
        Assert.Equal("SHARP", AndroidPicture.Next(AndroidPicture.Parse("FAST")).Name);
        Assert.Equal([1920, 1440, 960], AndroidPicture.Sizes.Select(AndroidPicture.LongSide));
    }
}
