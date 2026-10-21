using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Dbc;

/// <summary>
/// One value a module gives when asked: which module, which question, and where the value sits in
/// the answer (ADR-0073).
/// </summary>
/// <param name="Module">The module's request id: <c>7E0</c> for an answer heard on <c>7E8</c>.</param>
/// <param name="Mode">The service asked: <c>01</c> or <c>22</c>.</param>
/// <param name="Pid">The PID or data identifier asked for.</param>
/// <param name="Message">The answer message it is read from.</param>
/// <param name="Signal">The value, its position counted in the whole answer frame.</param>
public sealed record DiagnosticAnswer(ushort Module, byte Mode, ushort Pid, DbcMessage Message, DbcSignal Signal)
{
    /// <summary>Where the value's bytes begin in a single-frame answer: after the length, the echoed service and the identifier.</summary>
    public int DataStart => DiagnosticAnswers.DataStart(Mode);

    /// <summary>
    /// The value in an answer's payload — the bytes after the echoed mode and PID, as DashDeck's
    /// polling hands them over — or null when the payload is too short for it.
    /// </summary>
    public double? Decode(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[DataStart + payload.Length];
        payload.CopyTo(frame.AsSpan(DataStart));
        return Signal.Decode(frame);
    }

    /// <summary>
    /// The same value as DashDeck's catalog decodes it, or null when the catalog's byte-wise decode
    /// cannot say it: an Intel field across bytes, a field of three bytes, a signed field that does
    /// not fill its bytes, or one that starts before the data.
    /// </summary>
    public DecodeSpec? ToDecodeSpec() => DiagnosticAnswers.ToDecodeSpec(Signal, Mode);
}

/// <summary>What reading a database's answers found, and what it would not read as one.</summary>
public sealed record DiagnosticAnswerSet(IReadOnlyList<DiagnosticAnswer> Answers, IReadOnlyList<string> Problems);

/// <summary>
/// How a database holds values DashDeck asks for, rather than values a module sends by itself (ADR-0073).
/// </summary>
/// <remarks>
/// <b>A DBC describes frames, not questions.</b> So a value DashDeck asks for is written as the
/// frame that answers it: the module's answer identifier (its request id plus 8), with the
/// single-frame answer's bytes laid out the way ISO 15765-2 and the service define them —
/// <code>
///   byte 0      ISO-TP length
///   byte 1      the service, echoed + 0x40   — 41 for mode 01, 62 for mode 22
///   byte 2      mode 01: the PID             — mode 22: the identifier, bytes 2 and 3
///   byte 3 / 4  the value's bytes begin
/// </code>
/// Every answer of one module shares one message, so it is multiplexed twice — by the service,
/// then by the PID or identifier — with <c>SG_MUL_VAL_</c> lines saying which value hangs from
/// which: the extended multiplexing of the format, which tools that read OBD-II databases already
/// use. The question needs no field of its own: the module is the answer id less 8, the service is
/// the echo less 0x40, the identifier is the multiplexor's value.
/// <para>
/// A file with a single multiplexor across bytes 2 and 3 (or byte 2 alone, for mode 01) — the way
/// many hand-made databases are written — is read too. DashDeck writes the two-level form only.
/// </para>
/// <para>
/// An answer longer than one frame cannot be described — a DBC message is one frame — which leaves
/// out the VIN and part numbers, and no value DashDeck shows.
/// </para>
/// </remarks>
public static class DiagnosticAnswers
{
    /// <summary>The multiplexor on the echoed service, byte 1.</summary>
    public const string ServiceSignal = "Service";

    /// <summary>The second multiplexor for mode 01, the PID in byte 2.</summary>
    public const string PidSignal = "Pid01";

    /// <summary>The second multiplexor for mode 22, the identifier in bytes 2 and 3.</summary>
    public const string IdentifierSignal = "Did22";

    /// <summary>The ISO-TP length in byte 0. Written so a tool shows it; never read.</summary>
    public const string LengthSignal = "IsoTpLength";

    /// <summary>
    /// The database's own attribute saying which of DashDeck's buses its answers are heard on.
    /// One file is one bus, as DBC files are: a module address can be used on both.
    /// </summary>
    public const string BusAttribute = "DashDeckBus";

    /// <summary>The services DashDeck asks, and so the ones an answer can be read for.</summary>
    public static bool IsSupportedMode(byte mode) => mode is 0x01 or 0x22;

    /// <summary>How many bytes the identifier takes: one for a mode 01 PID, two for a mode 22 identifier.</summary>
    public static int IdentifierBytes(byte mode) => mode == 0x01 ? 1 : 2;

    /// <summary>Where the value's bytes begin in the answer frame.</summary>
    public static int DataStart(byte mode) => 2 + IdentifierBytes(mode);

    /// <summary>The answer identifier for a module's request id: <c>7E0</c> answers on <c>7E8</c>.</summary>
    public static uint AnswerId(ushort module) => (uint)module + 8;

    /// <summary>The name DashDeck gives a module's answer message: <c>Module_7E0</c>.</summary>
    public static string MessageName(ushort module) =>
        "Module_" + module.ToString("X3", CultureInfo.InvariantCulture);

    /// <summary>The bus a database says its answers are on, or null when it does not say or says something else.</summary>
    /// <remarks>
    /// An enum attribute's value is written as its index into the definition's names — <c>1</c> for
    /// <c>"Ms"</c> — which is what every tool expects; a name in quotes is read too.
    /// </remarks>
    public static CanBus? BusOf(DbcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.NetworkAttribute(BusAttribute) is not { } value)
        {
            return null;
        }

        var name = value.Text;

        if (!value.Quoted && int.TryParse(value.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var names = document.AttributeDefinitions.FirstOrDefault(d => d.Name == BusAttribute)?.Arguments ?? [];
            name = index < names.Count ? names[index].Text : "";
        }

        return name switch
        {
            "Hs" => CanBus.Hs,
            "Ms" => CanBus.Ms,
            _ => null,
        };
    }

    /// <summary>The same database, saying its answers are on that bus.</summary>
    /// <exception cref="InvalidOperationException">The database defines the attribute without that bus among its names.</exception>
    public static DbcDocument WithBus(DbcDocument document, CanBus bus)
    {
        ArgumentNullException.ThrowIfNull(document);

        var defined = document.WithDefinition(
            new DbcAttributeDefinition(DbcObjectKind.Network, BusAttribute, "ENUM", [DbcToken.String("Hs"), DbcToken.String("Ms")]));
        var names = defined.AttributeDefinitions.First(d => d.Name == BusAttribute).Arguments.Select(a => a.Text).ToList();
        var index = names.IndexOf(bus.ToString());

        if (index < 0)
        {
            throw new InvalidOperationException(Invariant($"{BusAttribute} is defined without {bus}"));
        }

        return defined.WithNetworkAttribute(BusAttribute, new DbcToken(index.ToString(CultureInfo.InvariantCulture), false));
    }

    /// <summary>Every answer value in a database, and why any answer-shaped message was not read as one.</summary>
    public static DiagnosticAnswerSet Read(DbcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var answers = new List<DiagnosticAnswer>();
        var problems = new List<string>();

        foreach (var message in document.Messages.Where(IsAnswerMessage))
        {
            var module = (ushort)(message.Id - 8);
            var multiplexors = message.Signals.Where(s => s.IsMultiplexor).ToList();

            if (multiplexors.Count == 0)
            {
                continue;
            }

            if (multiplexors.FirstOrDefault(s => s.MultiplexValue is null && Is(s, 15, 8)) is { } service)
            {
                TwoLevel(message, module, service, answers, problems);
            }
            else if (multiplexors is [{ MultiplexValue: null } only] && (Is(only, 23, 16) || Is(only, 23, 8)))
            {
                OneLevel(message, module, only, Is(only, 23, 16) ? (byte)0x22 : (byte)0x01, answers, problems);
            }
            else
            {
                problems.Add(Invariant($"{message.Name}: multiplexed, but not as answers to mode 01 or 22 are — left out"));
            }
        }

        return new DiagnosticAnswerSet(answers, problems);
    }

    /// <summary>
    /// The same database with one answer value added: its module's message made if it has none,
    /// with the service and identifier multiplexors, and a signal of the same name replaced.
    /// </summary>
    /// <param name="signal">The value, its position counted in the whole answer frame — see <see cref="Layout"/>.</param>
    /// <exception cref="ArgumentException">The mode is not 01 or 22, or the identifier does not fit it.</exception>
    /// <exception cref="InvalidOperationException">The module's message exists and is not in DashDeck's two-level form.</exception>
    public static DbcDocument Put(DbcDocument document, ushort module, byte mode, ushort pid, DbcSignal signal)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signal);

        if (!IsSupportedMode(mode))
        {
            throw new ArgumentException(Invariant($"mode {mode:X2} is not one DashDeck asks"), nameof(mode));
        }

        if (mode == 0x01 && pid > 0xFF)
        {
            throw new ArgumentException("a mode 01 PID is one byte", nameof(pid));
        }

        if (signal.Name is ServiceSignal or PidSignal or IdentifierSignal or LengthSignal)
        {
            throw new ArgumentException(Invariant($"{signal.Name} is one of the answer message's own signals"), nameof(signal));
        }

        var id = AnswerId(module);
        var message = document.Messages.FirstOrDefault(m => !m.IsExtended && m.Id == id)
            ?? new DbcMessage(id, MessageName(module), 8, "Vector__XXX")
            {
                Signals =
                [
                    new DbcSignal(LengthSignal, 7, 8, true, false, 1, 0, 0, 0, ""),
                    new DbcSignal(ServiceSignal, 15, 8, true, false, 1, 0, 0, 0, "") { IsMultiplexor = true },
                ],
            };

        if (message.Signal(ServiceSignal) is not { } service || !service.IsMultiplexor || service.MultiplexValue is not null || !Is(service, 15, 8))
        {
            throw new InvalidOperationException(
                Invariant($"{message.Name} is not multiplexed by the service in byte 1, so DashDeck will not add answers to it"));
        }

        var echo = (long)mode + 0x40;
        var switchName = mode == 0x01 ? PidSignal : IdentifierSignal;
        var signals = message.Signals.ToList();

        if (message.Signal(switchName) is not { } existing)
        {
            signals.Add(new DbcSignal(switchName, 23, IdentifierBytes(mode) * 8, true, false, 1, 0, 0, 0, "")
            {
                IsMultiplexor = true,
                MultiplexValue = echo,
                Switch = new DbcSwitch(ServiceSignal, [new DbcMultiplexRange(echo, echo)]),
            });
        }
        else if (!existing.IsMultiplexor || EchoUnder(existing, ServiceSignal) != echo || !Is(existing, 23, IdentifierBytes(mode) * 8))
        {
            throw new InvalidOperationException(Invariant($"{message.Name}.{switchName} is not where DashDeck puts it"));
        }

        var value = signal with
        {
            IsMultiplexor = false,
            MultiplexValue = pid,
            Switch = new DbcSwitch(switchName, [new DbcMultiplexRange(pid, pid)]),
        };

        var index = signals.FindIndex(s => s.Name == signal.Name);
        if (index < 0)
        {
            signals.Add(value);
        }
        else
        {
            signals[index] = value;
        }

        return document.WithMessage(message with { Signals = signals });
    }

    /// <summary>
    /// A catalog decode as a DBC signal in the answer frame, or null when it has no field to be:
    /// a mask that is not one run of bits, or a signed value with a mask.
    /// </summary>
    /// <remarks>
    /// The catalog keeps a masked value in place — PID 01's check-engine bit reads 128 — where a DBC
    /// field is shifted down to read 1, so the scale is multiplied by what the shift took away, and
    /// the two say the same number.
    /// </remarks>
    public static DbcSignal? Layout(DecodeSpec decode, byte mode, string name, double minimum = 0, double maximum = 0)
    {
        ArgumentNullException.ThrowIfNull(decode);

        if (decode.ByteLength is not (1 or 2 or 4) || decode.ByteOffset < 0)
        {
            return null;
        }

        var bits = decode.ByteLength * 8;
        int shift = 0, length = bits;

        if (decode.Mask is { } mask)
        {
            var kept = (ulong)mask & ((1UL << bits) - 1);
            if (kept == 0 || decode.Signed)
            {
                return null;
            }

            shift = System.Numerics.BitOperations.TrailingZeroCount(kept);
            var run = kept >> shift;
            if ((run & (run + 1)) != 0)
            {
                return null;
            }

            length = System.Numerics.BitOperations.PopCount(run);
        }

        var first = DataStart(mode) + decode.ByteOffset;
        var lowest = ((first + decode.ByteLength) * 8) - 1 - shift;   // big-endian place of the field's least significant bit
        var highest = lowest - length + 1;
        var start = ((highest / 8) * 8) + 7 - (highest % 8);

        return new DbcSignal(name, start, length, true, decode.Signed, decode.Scale * Math.Pow(2, shift), decode.Offset, minimum, maximum, decode.Unit);
    }

    /// <summary>A DBC signal in an answer frame as the catalog decodes it, or null when the catalog cannot say it.</summary>
    public static DecodeSpec? ToDecodeSpec(DbcSignal signal, byte mode)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (signal.Length is < 1 or > 32)
        {
            return null;
        }

        var places = DbcBits.BigEndianPlaces(signal.StartBit, signal.Length, signal.BigEndian).ToList();

        for (var i = 1; i < places.Count; i++)
        {
            if (places[i] != places[i - 1] + 1)
            {
                return null;   // an Intel field across a byte boundary is not one run read big-endian
            }
        }

        var firstByte = places[0] / 8;
        var lastByte = places[^1] / 8;
        var byteLength = lastByte - firstByte + 1;
        var offset = firstByte - DataStart(mode);

        if (byteLength is not (1 or 2 or 4) || offset < 0)
        {
            return null;
        }

        var shift = ((lastByte + 1) * 8) - 1 - places[^1];
        var whole = signal.Length == byteLength * 8;

        if (signal.Signed && !whole)
        {
            return null;
        }

        long? mask = whole ? null : ((1L << signal.Length) - 1) << shift;

        return new DecodeSpec(offset, byteLength, signal.Signed, signal.Scale / Math.Pow(2, shift), signal.Offset, signal.Unit, mask);
    }

    private static void TwoLevel(DbcMessage message, ushort module, DbcSignal service, List<DiagnosticAnswer> answers, List<string> problems)
    {
        var switches = new Dictionary<string, byte>();

        foreach (var sub in message.Signals.Where(s => s.IsMultiplexor && s != service))
        {
            if (EchoUnder(sub, service.Name) is not { } echo)
            {
                continue;
            }

            var mode = (byte)(echo - 0x40);

            if (echo is >= 0x40 and <= 0xFF && IsSupportedMode(mode) && Is(sub, 23, IdentifierBytes(mode) * 8))
            {
                switches[sub.Name] = mode;
            }
            else
            {
                problems.Add(Invariant($"{message.Name}.{sub.Name}: not a mode 01 PID in byte 2 or a mode 22 identifier in bytes 2–3 — its values are left out"));
            }
        }

        foreach (var signal in message.Signals.Where(s => !s.IsMultiplexor && s.MultiplexValue is not null))
        {
            if (signal.Switch is not { } hangs)
            {
                problems.Add(Invariant($"{message.Name}.{signal.Name}: no SG_MUL_VAL_ line says which multiplexor it hangs from — left out"));
                continue;
            }

            if (!switches.TryGetValue(hangs.Multiplexor, out var mode))
            {
                if (hangs.Multiplexor != service.Name)
                {
                    problems.Add(Invariant($"{message.Name}.{signal.Name}: hangs from {hangs.Multiplexor}, which is not a PID or identifier — left out"));
                }

                continue;
            }

            Add(message, module, mode, signal, answers, problems);
        }
    }

    private static void OneLevel(DbcMessage message, ushort module, DbcSignal multiplexor, byte mode, List<DiagnosticAnswer> answers, List<string> problems)
    {
        foreach (var signal in message.Signals.Where(s => !s.IsMultiplexor && s.MultiplexValue is not null))
        {
            if (signal.Switch is { } hangs && hangs.Multiplexor != multiplexor.Name)
            {
                problems.Add(Invariant($"{message.Name}.{signal.Name}: hangs from {hangs.Multiplexor}, which the message does not have — left out"));
                continue;
            }

            Add(message, module, mode, signal, answers, problems);
        }
    }

    private static void Add(DbcMessage message, ushort module, byte mode, DbcSignal signal, List<DiagnosticAnswer> answers, List<string> problems)
    {
        var at = signal.Switch?.Ranges ?? [new DbcMultiplexRange(signal.MultiplexValue!.Value, signal.MultiplexValue!.Value)];

        if (at is not [{ IsSingle: true } range] || range.From < 0 || range.From > (mode == 0x01 ? 0xFF : 0xFFFF))
        {
            problems.Add(Invariant($"{message.Name}.{signal.Name}: not at exactly one {(mode == 0x01 ? "PID" : "identifier")} — left out"));
            return;
        }

        var places = DbcBits.BigEndianPlaces(signal.StartBit, signal.Length, signal.BigEndian).ToList();
        if (places.Min() / 8 < DataStart(mode) || places.Max() / 8 >= Math.Min(message.Length, 8))
        {
            problems.Add(Invariant($"{message.Name}.{signal.Name}: not inside the value's bytes of a single-frame answer — left out"));
            return;
        }

        answers.Add(new DiagnosticAnswer(module, mode, (ushort)range.From, message, signal));
    }

    /// <summary>
    /// The service echo a second multiplexor hangs from the service at, or null when it does not.
    /// From its <c>SG_MUL_VAL_</c> line when it has one — some tools write it as a bare <c>M</c> and
    /// say where it hangs only there, as cantools does — otherwise from its own <c>m98M</c>.
    /// </summary>
    private static long? EchoUnder(DbcSignal sub, string service) =>
        sub.Switch is { } hangs
            ? hangs.Multiplexor == service && hangs.Ranges is [{ IsSingle: true } range] ? range.From : null
            : sub.MultiplexValue;

    /// <summary>A Motorola field at that start and length.</summary>
    private static bool Is(DbcSignal signal, int start, int length) =>
        signal.BigEndian && signal.StartBit == start && signal.Length == length && !signal.Signed;

    /// <summary>An 11-bit identifier with bit 3 set, in the range modules answer on (ADR-0035).</summary>
    private static bool IsAnswerMessage(DbcMessage message) =>
        !message.IsExtended && message.Id is >= 0x708 and <= 0x7FF && (message.Id & 0x8) != 0 && message.Length <= 8;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
