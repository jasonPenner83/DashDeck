using System.Runtime.InteropServices;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// Plays the phone's PCM through Windows' default output — the tablet's speakers, or whatever the
/// person chose in Windows: a Bluetooth link to the truck's stereo, an AUX cable (ADR-0057, F21).
/// </summary>
/// <remarks>
/// <b>Never the truck's audio system directly</b> (C3): DashDeck does not pick an output, Windows
/// does. One player per PCM format; Windows mixes them, so directions talk over the music the way
/// they do on a phone. The <c>waveOut</c> API is in every Windows since 95 and needs no package — a
/// queue of buffers handed to the device, each freed once it says it is done.
/// <para>
/// A player that falls more than <see cref="MaxQueued"/> behind drops what is waiting: late audio is
/// worse than a gap, because directions spoken after the turn are wrong.
/// </para>
/// </remarks>
public sealed class WaveOutPlayer : IDisposable
{
    /// <summary>Buffers allowed to wait for the device before the queue is thrown away.</summary>
    public const int MaxQueued = 32;

    private const int WaveMapper = -1;
    private const int CallbackNull = 0;
    private const int WhdrDone = 0x1;
    private const short WaveFormatPcm = 1;

    private readonly object _gate = new();
    private readonly Queue<(IntPtr Header, IntPtr Data)> _queued = new();
    private readonly int _headerSize = Marshal.SizeOf<WaveHeader>();

    private IntPtr _device;
    private bool _disposed;

    private WaveOutPlayer(IntPtr device, PcmFormat format)
    {
        _device = device;
        Format = format;
    }

    /// <summary>What it plays.</summary>
    public PcmFormat Format { get; }

    /// <summary>Open the default output for a format, or null (with why) when Windows refuses.</summary>
    public static WaveOutPlayer? Open(PcmFormat format, out string? problem)
    {
        var wave = new WaveFormat
        {
            FormatTag = WaveFormatPcm,
            Channels = (short)format.Channels,
            SamplesPerSec = format.SampleRate,
            BitsPerSample = 16,
            BlockAlign = (short)(format.Channels * 2),
            AvgBytesPerSec = format.BytesPerSecond,
        };

        try
        {
            var result = waveOutOpen(out var device, WaveMapper, ref wave, IntPtr.Zero, IntPtr.Zero, CallbackNull);
            if (result != 0)
            {
                problem = $"Windows would not open its audio output (waveOut {result}).";
                return null;
            }

            problem = null;
            return new WaveOutPlayer(device, format);
        }
        catch (DllNotFoundException)
        {
            problem = "No Windows audio output (winmm) on this machine.";
            return null;
        }
    }

    /// <summary>Queue PCM to play. Copied; the caller's buffer is free when this returns.</summary>
    public void Play(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Reclaim();

            if (_queued.Count >= MaxQueued)
            {
                waveOutReset(_device);
                Reclaim();
            }

            var data = Marshal.AllocHGlobal(pcm.Length);
            Marshal.Copy(pcm.ToArray(), 0, data, pcm.Length);

            var header = Marshal.AllocHGlobal(_headerSize);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = pcm.Length }, header, false);

            if (waveOutPrepareHeader(_device, header, _headerSize) != 0)
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
                return;
            }

            waveOutWrite(_device, header, _headerSize);
            _queued.Enqueue((header, data));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Reset hands every buffer back as done; then they can be freed.
            waveOutReset(_device);
            Reclaim(force: true);
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
    }

    private void Reclaim(bool force = false)
    {
        while (_queued.TryPeek(out var next))
        {
            var flags = Marshal.PtrToStructure<WaveHeader>(next.Header).Flags;
            if (!force && (flags & WhdrDone) == 0)
            {
                return;
            }

            _queued.Dequeue();
            waveOutUnprepareHeader(_device, next.Header, _headerSize);
            Marshal.FreeHGlobal(next.Header);
            Marshal.FreeHGlobal(next.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);

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

/// <summary>
/// The phone's audio, routed: a <see cref="WaveOutPlayer"/> for each format the dongle sends, opened
/// on first use and closed together.
/// </summary>
public sealed class ProjectionAudio : IDisposable
{
    private readonly Dictionary<PcmFormat, WaveOutPlayer> _players = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Why a format could not be played, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Audio packets played.</summary>
    public long Played { get; private set; }

    /// <summary>Play one audio message's samples; commands and unknown formats are passed over.</summary>
    public void Play(DongleAudio audio)
    {
        if (audio.Samples.IsEmpty || audio.Format is not { } format)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_players.TryGetValue(format, out var player))
            {
                player = WaveOutPlayer.Open(format, out var problem);
                if (player is null)
                {
                    Problem = problem;
                    return;
                }

                _players[format] = player;
            }

            player.Play(audio.Samples.Span);
            Played++;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var player in _players.Values)
            {
                player.Dispose();
            }

            _players.Clear();
        }
    }
}
