using System.Runtime.InteropServices;

namespace DashDeck.WinAudio;

/// <summary>
/// The outputs and inputs Windows will admit to through <c>waveOut</c> and <c>waveIn</c>.
/// </summary>
/// <remarks>
/// This is the whole of Q34. The legacy wave API names a device in a fixed 32-character field,
/// which in practice means a <b>truncated</b> name and no stable identifier — "Speakers (Realtek(R)
/// Audi" is a real example. There is no promise that the list maps onto the endpoints Windows shows
/// in its own settings. The tool prints what it is given and lets the person choose; if the
/// built-in speaker cannot be told apart here, that is the answer to Q34, not a bug to work around.
/// </remarks>
public static class WaveDevices
{
    /// <summary>The device id meaning "whatever Windows is currently defaulting to".</summary>
    public const int Mapper = -1;

    /// <summary>Outputs, in the order <c>waveOut</c> reports them.</summary>
    public static IReadOnlyList<WaveDevice> Outputs()
    {
        var devices = new List<WaveDevice>();
        var count = waveOutGetNumDevs();

        for (var id = 0; id < count; id++)
        {
            if (waveOutGetDevCaps(id, out var caps, Marshal.SizeOf<WaveOutCaps>()) == 0)
            {
                devices.Add(new WaveDevice(id, caps.Name, caps.Channels));
            }
        }

        return devices;
    }

    /// <summary>Inputs, in the order <c>waveIn</c> reports them.</summary>
    public static IReadOnlyList<WaveDevice> Inputs()
    {
        var devices = new List<WaveDevice>();
        var count = waveInGetNumDevs();

        for (var id = 0; id < count; id++)
        {
            if (waveInGetDevCaps(id, out var caps, Marshal.SizeOf<WaveInCaps>()) == 0)
            {
                devices.Add(new WaveDevice(id, caps.Name, caps.Channels));
            }
        }

        return devices;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public short Mid;
        public short Pid;
        public int DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public int Formats;
        public short Channels;
        public short Reserved;
        public int Support;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public short Mid;
        public short Pid;
        public int DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public int Formats;
        public short Channels;
        public short Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
    private static extern int waveOutGetDevCaps(int device, out WaveOutCaps caps, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")]
    private static extern int waveInGetDevCaps(int device, out WaveInCaps caps, int size);
}

/// <summary>One output or input as the legacy wave API describes it.</summary>
/// <param name="Id">Index to pass to <c>waveOutOpen</c> or <c>waveInOpen</c>.</param>
/// <param name="Name">The name, as truncated to 31 characters by the API.</param>
/// <param name="Channels">Channels it claims to support.</param>
public readonly record struct WaveDevice(int Id, string Name, int Channels);
