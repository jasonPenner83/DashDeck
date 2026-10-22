using DashDeck.Core.Emulation;
using DashDeck.Emulator;

namespace DashDeck.Tests;

/// <summary>Android's internet, how the emulator draws, and its pixels (ADR-0075 addendum).</summary>
public class EmulatorTuningTests
{
    [Fact]
    public void Android_gets_the_tablet_s_own_dns_then_public_ones()
    {
        Assert.Equal(["192.168.1.1", "1.1.1.1", "8.8.8.8"], EmulatorNetwork.DnsServers(["192.168.1.1"]));

        // The hypervisor's switch, loopback, link-local, IPv6 and duplicates are not the internet.
        Assert.Equal(
            ["172.20.10.1", "1.1.1.1", "8.8.8.8"],
            EmulatorNetwork.DnsServers(["fec0:0:0:ffff::1", "127.0.0.1", "169.254.10.2", "172.20.10.1", "172.20.10.1", "0.0.0.0", "nonsense"]));

        // Nothing of its own (offline, or the adapters could not be read): the public ones alone.
        Assert.Equal(["1.1.1.1", "8.8.8.8"], EmulatorNetwork.DnsServers([]));

        // Four at most, two of them the tablet's.
        Assert.Equal(["10.0.0.1", "10.0.0.2", "1.1.1.1", "8.8.8.8"], EmulatorNetwork.DnsServers(["10.0.0.1", "10.0.0.2", "10.0.0.3"]));
    }

    [Fact]
    public void The_dash_asks_for_the_gpu_unless_told_otherwise()
    {
        // The emulator chose software on the Surface, so by default the dash asks for the GPU — and
        // falls back to the emulator's own choice if it will not start that way.
        Assert.Equal(new GpuChoice("host", true), EmulatorGpu.Choose("auto"));
        Assert.Equal(new GpuChoice("host", true), EmulatorGpu.Choose(null));
        Assert.Equal(new GpuChoice("host", true), EmulatorGpu.Choose("turbo"));

        Assert.Equal(GpuChoice.Emulator, EmulatorGpu.Choose("emulator"));
        Assert.Equal(new GpuChoice("host", false), EmulatorGpu.Choose("Host"));
        Assert.Equal(new GpuChoice("angle_indirect", false), EmulatorGpu.Choose("angle_indirect"));
    }

    [Theory]
    [InlineData("sw", true)]
    [InlineData("swiftshader_indirect", true)]
    [InlineData("swangle_indirect", true)]
    [InlineData("host", false)]
    [InlineData("angle_indirect", false)]
    public void Software_is_known_by_any_of_its_names(string mode, bool software) =>
        Assert.Equal(software, EmulatorGpu.IsSoftware(mode));

    [Fact]
    public void How_the_emulator_draws_is_read_from_what_it_printed()
    {
        Assert.Equal(
            "host · Intel(R) Iris(R) Plus Graphics",
            EmulatorGpu.Renderer(
            [
                "INFO    | Graphics backend: gfxstream",
                "INFO    | GPU emulation enabled using 'host' mode",
                "INFO    | OpenGL Vendor=[Google (Intel)] OpenGL Renderer=[Android Emulator OpenGL ES Translator (Intel(R) Iris(R) Plus Graphics)]",
            ]));

        Assert.Equal(
            "swiftshader_indirect (software — slow)",
            EmulatorGpu.Renderer(["WARNING | Your GPU is not supported; GPU emulation enabled using 'swiftshader_indirect' mode"]));

        // What the Surface showed: the shortest name of all.
        Assert.Equal("sw (software — slow)", EmulatorGpu.Renderer(["INFO    | GPU emulation enabled using 'sw' mode"]));

        Assert.Null(EmulatorGpu.Renderer(["INFO    | Advertising in: C:\\Users\\x\\AppData\\Local\\Temp\\avd\\running\\pid_1.ini"]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(1272 * 3 + 1)]
    public void Pixels_four_at_a_time_come_out_as_one_at_a_time_would(int pixels)
    {
        var rgb = new byte[pixels * 3];
        new Random(pixels).NextBytes(rgb);
        var fast = new byte[pixels * 4];

        FramePixels.RgbToBgr32(rgb, fast);

        for (var i = 0; i < pixels; i++)
        {
            Assert.Equal((rgb[(i * 3) + 2], rgb[(i * 3) + 1], rgb[i * 3]), (fast[i * 4], fast[(i * 4) + 1], fast[(i * 4) + 2]));
        }
    }
}
