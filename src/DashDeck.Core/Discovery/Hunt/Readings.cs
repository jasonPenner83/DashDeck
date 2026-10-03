using System.Globalization;

namespace DashDeck.Core.Discovery.Hunt;

/// <summary>One way of reading a value out of an answer's bytes: which bytes, and signed or not.</summary>
/// <param name="Offset">The first byte read.</param>
/// <param name="Length">How many bytes, big-endian: 1, 2 or 4.</param>
/// <param name="Signed">Read as two's complement.</param>
public sealed record Reading(int Offset, int Length, bool Signed)
{
    /// <summary>The value these bytes give, or null when the answer is too short.</summary>
    public long? Read(byte[] data)
    {
        if (Offset + Length > data.Length)
        {
            return null;
        }

        long value = 0;
        for (var i = 0; i < Length; i++)
        {
            value = (value << 8) | data[Offset + i];
        }

        if (Signed)
        {
            var bits = Length * 8;
            if ((value & (1L << (bits - 1))) != 0)
            {
                value -= 1L << bits;
            }
        }

        return value;
    }

    /// <summary>How the CSV and the screen name it: <c>u16@0</c>, <c>s8@2</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(Signed ? 's' : 'u')}{Length * 8}@{Offset}");

    /// <summary>
    /// Every sensible reading of an answer <paramref name="length"/> bytes long: the whole thing
    /// (when 1, 2 or 4 bytes), each byte, and each pair — unsigned, and signed for 2 bytes and more.
    /// </summary>
    public static IReadOnlyList<Reading> All(int length)
    {
        var all = new List<Reading>();

        void Add(int offset, int size)
        {
            all.Add(new Reading(offset, size, false));
            if (size >= 2)
            {
                all.Add(new Reading(offset, size, true));
            }
        }

        if (length is 1 or 2 or 4)
        {
            Add(0, length);
        }

        if (length > 1)
        {
            for (var i = 0; i < Math.Min(length, 8); i++)
            {
                Add(i, 1);
            }

            for (var i = 0; i + 1 < Math.Min(length, 8); i++)
            {
                if (!(length == 2 && i == 0))
                {
                    Add(i, 2);
                }
            }
        }

        return all;
    }
}
