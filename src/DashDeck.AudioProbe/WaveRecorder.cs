using System.Runtime.InteropServices;

namespace DashDeck.AudioProbe;

/// <summary>
/// Records one continuous stretch of microphone audio, and says when it started.
/// </summary>
/// <remarks>
/// <b>One capture for the whole measurement, deliberately.</b> Everything this tool measures is a
/// difference between two arrivals, and both the moment capture began and however long Windows takes
/// to hand captured audio over are constant within a single session — so both cancel. Recording
/// twice would put them back in as error.
/// <para>
/// The buffers are all queued before the capture starts and read back in order afterwards, rather
/// than being drained as they fill. For a measurement of a few seconds that is simpler and has no
/// chance of a gap in the middle, which would move every arrival after it.
/// </para>
/// </remarks>
public sealed class WaveRecorder : IDisposable
{
    private const int BufferMilliseconds = 100;

    private readonly int _sampleRate;
    private readonly List<(IntPtr Header, IntPtr Data)> _buffers = [];
    private readonly int _headerSize = Marshal.SizeOf<WaveIo.WaveHeader>();
    private readonly int _bufferBytes;

    private IntPtr _device;
    private bool _disposed;

    private WaveRecorder(IntPtr device, int sampleRate, int bufferBytes)
    {
        _device = device;
        _sampleRate = sampleRate;
        _bufferBytes = bufferBytes;
    }

    /// <summary>Open an input and queue enough buffers for <paramref name="duration"/>.</summary>
    /// <returns>The recorder, or null with <paramref name="problem"/> set.</returns>
    public static WaveRecorder? Open(int deviceId, int sampleRate, TimeSpan duration, out string? problem)
    {
        var format = WaveIo.Format(sampleRate);

        try
        {
            var result = waveInOpen(out var device, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, WaveIo.CallbackNull);
            if (result != 0)
            {
                problem = $"Windows would not open input device {deviceId} (waveIn {result}). "
                    + "A rate it does not offer, or another program holding the microphone.";
                return null;
            }

            var bufferBytes = sampleRate * 2 * BufferMilliseconds / 1000;
            var recorder = new WaveRecorder(device, sampleRate, bufferBytes);

            // A second of slack past what was asked for: better a long tail than a probe that
            // arrives after the recording stopped.
            var count = (int)Math.Ceiling((duration.TotalMilliseconds + 1000) / BufferMilliseconds);
            for (var i = 0; i < count; i++)
            {
                if (!recorder.Queue())
                {
                    recorder.Dispose();
                    problem = "Windows refused a capture buffer.";
                    return null;
                }
            }

            problem = null;
            return recorder;
        }
        catch (DllNotFoundException)
        {
            problem = "No Windows audio (winmm) on this machine. This tool only runs on Windows.";
            return null;
        }
    }

    /// <summary>Start capturing. The returned stopwatch is the session's time zero.</summary>
    public System.Diagnostics.Stopwatch Start()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        waveInStart(_device);
        return clock;
    }

    /// <summary>Stop capturing and return everything recorded, in order.</summary>
    public float[] Stop()
    {
        waveInStop(_device);
        waveInReset(_device);

        var samples = new List<float>();
        var block = new float[_bufferBytes / 2];

        foreach (var (header, data) in _buffers)
        {
            var recorded = Marshal.PtrToStructure<WaveIo.WaveHeader>(header).BytesRecorded;
            if (recorded <= 0)
            {
                continue;
            }

            var pcm = new byte[recorded];
            Marshal.Copy(data, pcm, 0, recorded);
            var span = block.AsSpan(0, recorded / 2);
            WaveIo.FromPcm(pcm, span);
            samples.AddRange(span);
        }

        return [.. samples];
    }

    /// <summary>Samples per second, so a time can be turned into an index.</summary>
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        waveInReset(_device);

        foreach (var (header, data) in _buffers)
        {
            waveInUnprepareHeader(_device, header, _headerSize);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }

        _buffers.Clear();
        waveInClose(_device);
        _device = IntPtr.Zero;
    }

    private bool Queue()
    {
        var data = Marshal.AllocHGlobal(_bufferBytes);
        var header = Marshal.AllocHGlobal(_headerSize);
        Marshal.StructureToPtr(new WaveIo.WaveHeader { Data = data, BufferLength = _bufferBytes }, header, false);

        if (waveInPrepareHeader(_device, header, _headerSize) != 0 ||
            waveInAddBuffer(_device, header, _headerSize) != 0)
        {
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
            return false;
        }

        _buffers.Add((header, data));
        return true;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr device, int deviceId, ref WaveIo.WaveFormat format, IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr device);
}
