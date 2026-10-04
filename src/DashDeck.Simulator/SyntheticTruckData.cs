using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DashDeck.Abstractions;

namespace DashDeck.Simulator;

/// <summary>
/// The synthetic truck's own invented world — its modules, their identifiers, its broadcast
/// frames and its VIN — read from <c>catalog/simulator/synthetic-truck.json</c> (ADR-0052).
/// </summary>
/// <remarks>
/// Nothing here is a real vehicle's. The truck's standard values and placeholders are not here
/// at all: those come from the signal catalog (<see cref="SyntheticTransport"/>).
/// </remarks>
public sealed record SyntheticTruckData
{
    public const string FileName = "synthetic-truck.json";

    /// <summary>The truck's VIN, or null for none (mode 09 then answers NO DATA).</summary>
    public string? Vin { get; init; }

    public IReadOnlyList<SyntheticModule> Modules { get; init; } = [];

    public IReadOnlyList<SyntheticFrame> Broadcast { get; init; } = [];

    /// <summary>Nothing: no modules, no frames, no VIN.</summary>
    public static SyntheticTruckData Empty { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <exception cref="InvalidDataException">The text is not the file's shape.</exception>
    public static SyntheticTruckData Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SyntheticTruckData>(json, JsonOptions)
                ?? throw new InvalidDataException($"{FileName}: empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName}: {ex.Message}", ex);
        }
    }

    private static SyntheticTruckData? _shipped;

    /// <summary>
    /// The shipped file, found beside or above the program like the rest of the catalog; empty
    /// when it cannot be found or read.
    /// </summary>
    public static SyntheticTruckData Shipped()
    {
        if (_shipped is not null)
        {
            return _shipped;
        }

        var path = SyntheticCatalog.Find(Path.Combine("simulator", FileName));
        try
        {
            _shipped = path is null ? Empty : Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _shipped = Empty;
        }

        return _shipped;
    }

    /// <summary>The module at a bus and request id, or null.</summary>
    public SyntheticModule? ModuleAt(CanBus bus, ushort address) =>
        Modules.FirstOrDefault(m => m.Bus == bus && Hex(m.Address) == address);

    internal static ushort? Hex(string? text) =>
        ushort.TryParse((text ?? "").Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>A synthetic module: its bus and request id, the identity it gives, and its identifiers.</summary>
public sealed record SyntheticModule
{
    public CanBus Bus { get; init; }

    public required string Address { get; init; }

    /// <summary>What it answers the identity question with, or null to decline it.</summary>
    public string? Identity { get; init; }

    public IReadOnlyList<SyntheticIdentifier> Identifiers { get; init; } = [];

    /// <summary>Identifiers it has but refuses without security access.</summary>
    public IReadOnlyList<string> Locked { get; init; } = [];
}

/// <summary>One identifier a synthetic module answers: fixed text, or a quantity encoded.</summary>
public sealed record SyntheticIdentifier : SyntheticByteSource
{
    public required string Did { get; init; }

    /// <summary>ASCII to answer with; <c>$vin</c> is the truck's VIN.</summary>
    public string? Text { get; init; }
}

/// <summary>A broadcast frame: its bus, id, how often, and its bytes.</summary>
public sealed record SyntheticFrame
{
    public CanBus Bus { get; init; }

    public required string Id { get; init; }

    public int PeriodMs { get; init; } = 100;

    /// <summary>Sent only when its bytes change, rather than on a period.</summary>
    public bool OnChange { get; init; }

    /// <summary>Each a number, or an object describing what goes there.</summary>
    public IReadOnlyList<JsonElement> Bytes { get; init; } = [];
}

/// <summary>Where some bytes come from: a quantity encoded, bits, a counter, noise, or an XOR.</summary>
public record SyntheticByteSource
{
    /// <summary>A quantity the synthetic truck knows, by name.</summary>
    public string? Value { get; init; }

    public int ByteLength { get; init; } = 1;

    public double Scale { get; init; } = 1;

    public double Offset { get; init; }

    /// <summary>Sensor noise added to the value.</summary>
    public double Jitter { get; init; }

    /// <summary><c>seat</c>: heat 1–3 in the low nibble, cooling 1–3 in the high one.</summary>
    public string? Encoding { get; init; }

    public bool Random { get; init; }

    /// <summary>Single bits, each lit when its value is not zero.</summary>
    public IReadOnlyList<SyntheticBit> Bits { get; init; } = [];

    /// <summary>A rolling counter that wraps at this.</summary>
    public int? Counter { get; init; }

    /// <summary>The XOR of the bytes at these positions in the same frame.</summary>
    public IReadOnlyList<int> Xor { get; init; } = [];
}

/// <summary>One bit of a byte: lit when the named value is not zero.</summary>
public sealed record SyntheticBit(string Value, int Bit);

/// <summary>Finding the shipped catalog files from wherever the program runs.</summary>
public static class SyntheticCatalog
{
    /// <summary>A file under <c>catalog/</c>, beside or above the program, or null.</summary>
    public static string? Find(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "catalog", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }
}

/// <summary>Turning the data file's descriptions into bytes, against the truck's quantities.</summary>
internal static class SyntheticEncoding
{
    /// <summary>Encode a quantity as the description says, big-endian; null when the truck lacks it.</summary>
    public static byte[]? Encode(SyntheticByteSource source, SimulatedF150 truck)
    {
        if (source.Random)
        {
            return [(byte)truck.Random.Next(256)];
        }

        if (source.Bits.Count > 0)
        {
            byte b = 0;
            foreach (var bit in source.Bits)
            {
                if (truck.Value(bit.Value) is { } v && v != 0)
                {
                    b |= (byte)(1 << bit.Bit);
                }
            }

            return [b];
        }

        if (source.Value is null || truck.Value(source.Value) is not { } value)
        {
            return null;
        }

        if (source.Jitter > 0)
        {
            value = truck.Jitter(value, source.Jitter);
        }

        if (source.Encoding == "seat")
        {
            var level = (int)Math.Round(value);
            return [level >= 0 ? (byte)level : (byte)(-level << 4)];
        }

        return Raw((value - source.Offset) / source.Scale, source.ByteLength, signed: false);
    }

    /// <summary>A raw number as big-endian bytes, clamped to what fits.</summary>
    public static byte[] Raw(double raw, int length, bool signed)
    {
        var bits = length * 8;
        var min = signed ? -(1L << (bits - 1)) : 0;
        var max = signed ? (1L << (bits - 1)) - 1 : (1L << bits) - 1;
        var n = (long)Math.Clamp(Math.Round(raw), min, max);

        var bytes = new byte[length];
        for (var i = length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(n & 0xFF);
            n >>= 8;
        }

        return bytes;
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
