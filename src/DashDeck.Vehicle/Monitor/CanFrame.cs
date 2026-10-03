using System.Globalization;
using DashDeck.Abstractions;

namespace DashDeck.Vehicle.Monitor;

/// <summary>One frame heard on a bus while listening (ADR-0044).</summary>
/// <param name="At">When it arrived, from the start of the listen.</param>
/// <param name="Bus">The bus it was heard on.</param>
/// <param name="Id">Its CAN identifier: 11-bit, or 29-bit on a bus that uses them.</param>
/// <param name="Data">Its data bytes, up to eight.</param>
public sealed record CanFrame(TimeSpan At, CanBus Bus, uint Id, byte[] Data)
{
    /// <summary>The identifier as the adapter prints it: three hex digits, or eight for a 29-bit one.</summary>
    public string IdText => Id > 0x7FF ? Id.ToString("X8", CultureInfo.InvariantCulture) : Id.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>The data as spaced hex, <c>01 A0 FF</c>.</summary>
    public string DataText => string.Join(' ', Data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}

/// <summary>Reads the lines an ELM327/STN adapter prints in monitor mode.</summary>
public static class MonitorLine
{
    /// <summary>
    /// Read one monitor line — with headers on and CAN formatting off, an identifier then the
    /// data: <c>3B3 01 02 03 04 05 06 07 08</c>, or <c>3B30102030405060708</c> with spaces off.
    /// </summary>
    /// <returns>False for anything else: a status line, a buffer warning, garbage.</returns>
    public static bool TryParse(string line, out uint id, out byte[] data)
    {
        id = 0;
        data = [];

        var text = line.Trim();
        if (text.Length < 3)
        {
            return false;
        }

        string idText;
        string dataText;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            idText = parts[0];
            if (parts.Skip(1).Any(p => p.Length != 2))
            {
                return false;
            }

            dataText = string.Concat(parts.Skip(1));
        }
        else
        {
            // Spaces off: an 11-bit identifier is three digits and the data an even number after
            // it, so the whole line is odd; a 29-bit one is eight digits and the line even.
            var idLength = text.Length % 2 == 1 ? 3 : 8;
            if (text.Length < idLength)
            {
                return false;
            }

            idText = text[..idLength];
            dataText = text[idLength..];
        }

        if (idText.Length is not (3 or 8) ||
            !uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) ||
            dataText.Length % 2 != 0 || dataText.Length > 16)
        {
            return false;
        }

        try
        {
            data = Convert.FromHexString(dataText);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for the lines that mean frames were lost: the adapter's buffer filled, or the serial
    /// link could not keep up with the bus.
    /// </summary>
    public static bool IsOverflow(string line) =>
        line.Contains("BUFFER FULL", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("OVERFLOW", StringComparison.OrdinalIgnoreCase);
}
