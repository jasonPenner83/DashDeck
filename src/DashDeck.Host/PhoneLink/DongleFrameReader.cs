namespace DashDeck.Host.PhoneLink;

/// <summary>
/// Turns the dongle's raw bytes back into messages, however the reads happened to cut them.
/// </summary>
/// <remarks>
/// A USB bulk read ends where the transfer ends, not where a message does: a header may arrive alone,
/// a payload in pieces, two messages in one read. This keeps what it has and hands out whole
/// messages. When the stream stops making sense — a header that is not one — it drops a byte at a
/// time until it finds the magic again, rather than reading garbage as a giant payload. Every byte it
/// throws away is counted.
/// </remarks>
public sealed class DongleFrameReader
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _count;

    /// <summary>Bytes thrown away looking for the next header.</summary>
    public long SkippedBytes { get; private set; }

    /// <summary>
    /// How many more bytes would complete the next message: the rest of a header, or the rest of the
    /// payload a header announced. One when the bytes held are not a header yet (<see cref="Next"/>
    /// drops them a byte at a time).
    /// </summary>
    /// <remarks>
    /// The USB transport reads exactly this much, the way the community driver reads a header and then
    /// its payload: a bulk read asked for more than one transfer holds can wait for the next.
    /// </remarks>
    public int Needed
    {
        get
        {
            if (_count < DongleMessage.HeaderLength)
            {
                return DongleMessage.HeaderLength - _count;
            }

            return DongleMessage.TryReadHeader(_buffer.AsSpan(0, _count), out _, out var length)
                ? Math.Max(1, DongleMessage.HeaderLength + length - _count)
                : 1;
        }
    }

    /// <summary>Add bytes as they were read.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_count + data.Length > _buffer.Length)
        {
            var bigger = new byte[Math.Max(_buffer.Length * 2, _count + data.Length)];
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _count);
            _buffer = bigger;
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>The next whole message, or null until there is one.</summary>
    public DongleMessage? Next()
    {
        while (_count >= DongleMessage.HeaderLength)
        {
            if (!DongleMessage.TryReadHeader(_buffer.AsSpan(0, _count), out var type, out var length))
            {
                // Out of step: drop one byte and look again.
                Consume(1);
                SkippedBytes++;
                continue;
            }

            if (_count < DongleMessage.HeaderLength + length)
            {
                return null;
            }

            var payload = _buffer.AsSpan(DongleMessage.HeaderLength, length).ToArray();
            Consume(DongleMessage.HeaderLength + length);
            return new DongleMessage(type, payload);
        }

        return null;
    }

    private void Consume(int bytes)
    {
        Buffer.BlockCopy(_buffer, bytes, _buffer, 0, _count - bytes);
        _count -= bytes;
    }
}
