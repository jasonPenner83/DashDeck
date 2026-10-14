using System.Runtime.InteropServices;

namespace DashDeck.WinAudio;

/// <summary>Shared bits of the legacy wave API: the format struct and the buffer header.</summary>
internal static class WaveIo
{
    internal const int CallbackNull = 0;
    internal const int WhdrDone = 0x1;
    internal const short FormatPcm = 1;

    /// <summary>16-bit mono at the probe's rate — what both ends of this measurement speak.</summary>
    internal static WaveFormat Format(int sampleRate) => new()
    {
        FormatTag = FormatPcm,
        Channels = 1,
        SamplesPerSec = sampleRate,
        AvgBytesPerSec = sampleRate * 2,
        BlockAlign = 2,
        BitsPerSample = 16,
    };

    /// <summary>Float samples in -1..1 to the 16-bit PCM the device wants.</summary>
    internal static byte[] ToPcm(ReadOnlySpan<float> samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>16-bit PCM back to floats.</summary>
    internal static void FromPcm(ReadOnlySpan<byte> pcm, Span<float> samples)
    {
        var count = Math.Min(samples.Length, pcm.Length / 2);
        for (var i = 0; i < count; i++)
        {
            samples[i] = (short)(pcm[i * 2] | (pcm[(i * 2) + 1] << 8)) / (float)short.MaxValue;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
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
    internal struct WaveHeader
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
}
