using System.IO;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// A one-way pipe of H.264 from the dongle to the decoder: written from the USB thread, read by
/// LibVLC's own, and blocking the reader until there is something to read.
/// </summary>
/// <remarks>
/// <b>It never lets the picture fall behind.</b> A decoder that stalls — the tablet busy, the stage
/// hidden — would otherwise be fed a growing backlog and show the phone's screen from seconds ago.
/// Past <see cref="MaxQueuedBytes"/> it throws the queue away: the picture smears until the phone's
/// next key frame, which beats a projection that lags behind the finger on it.
/// </remarks>
public sealed class H264Pipe : Stream
{
    /// <summary>How much may wait for the decoder before it is thrown away.</summary>
    public const int MaxQueuedBytes = 4 * 1024 * 1024;

    private readonly Queue<byte[]> _chunks = new();
    private readonly object _gate = new();

    private byte[]? _current;
    private int _offset;
    private int _queued;
    private bool _completed;

    /// <summary>Bytes thrown away because the decoder fell behind.</summary>
    public long DroppedBytes { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Add H.264 as it arrived.</summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            if (_queued + data.Length > MaxQueuedBytes)
            {
                DroppedBytes += _queued;
                _chunks.Clear();
                _queued = 0;
            }

            _chunks.Enqueue(data.ToArray());
            _queued += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>No more is coming: the reader gets what is left, then the end.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (_chunks.TryDequeue(out var next))
                {
                    _queued -= next.Length;
                    _current = next;
                    _offset = 0;
                    break;
                }

                if (_completed)
                {
                    return 0;
                }

                Monitor.Wait(_gate);
            }

            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Complete();
        base.Dispose(disposing);
    }
}
