using System.Runtime.InteropServices;

namespace DashDeck.WinAudio;

/// <summary>
/// Plays one buffer to one named output and reports the instant it was handed over.
/// </summary>
/// <remarks>
/// The handover instant is the measurement's reference. Everything after it — the mixer, the
/// Bluetooth encoder, the air, the stereo's own buffer and processing — is what is being measured,
/// and none of it can be observed from here. So the only honest reference is "the moment this
/// process let go of the audio", taken from the same stopwatch that timed the capture.
/// </remarks>
public sealed class WavePlayer : IDisposable
{
    private readonly int _headerSize = Marshal.SizeOf<WaveIo.WaveHeader>();
    private readonly List<(IntPtr Header, IntPtr Data)> _played = [];

    private IntPtr _device;
    private bool _disposed;

    private WavePlayer(IntPtr device) => _device = device;

    /// <summary>Open an output, or null with <paramref name="problem"/> set.</summary>
    public static WavePlayer? Open(int deviceId, int sampleRate, out string? problem)
    {
        var format = WaveIo.Format(sampleRate);

        try
        {
            var result = waveOutOpen(out var device, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, WaveIo.CallbackNull);
            if (result != 0)
            {
                problem = $"Windows would not open output device {deviceId} (waveOut {result}).";
                return null;
            }

            problem = null;
            return new WavePlayer(device);
        }
        catch (DllNotFoundException)
        {
            problem = "No Windows audio (winmm) on this machine. This tool only runs on Windows.";
            return null;
        }
    }

    /// <summary>Hand <paramref name="samples"/> to the device; returns when it has been accepted.</summary>
    /// <returns>Whether the write was accepted.</returns>
    public bool Play(ReadOnlySpan<float> samples)
    {
        var pcm = WaveIo.ToPcm(samples);
        var data = Marshal.AllocHGlobal(pcm.Length);
        Marshal.Copy(pcm, 0, data, pcm.Length);

        var header = Marshal.AllocHGlobal(_headerSize);
        Marshal.StructureToPtr(new WaveIo.WaveHeader { Data = data, BufferLength = pcm.Length }, header, false);

        if (waveOutPrepareHeader(_device, header, _headerSize) != 0 ||
            waveOutWrite(_device, header, _headerSize) != 0)
        {
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            return false;
        }

        _played.Add((header, data));
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        waveOutReset(_device);

        foreach (var (header, data) in _played)
        {
            waveOutUnprepareHeader(_device, header, _headerSize);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }

        _played.Clear();
        waveOutClose(_device);
        _device = IntPtr.Zero;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveIo.WaveFormat format, IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}
