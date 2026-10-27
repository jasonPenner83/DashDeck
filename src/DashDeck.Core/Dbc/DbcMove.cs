using System.Globalization;
using System.Text;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Dbc;

/// <summary>A signal that stayed in the catalog, and why.</summary>
public sealed record DbcStay(string Id, string Why);

/// <summary>
/// What moving the person's signals into their databases comes to: the overlay with the moved
/// entries now referring, the databases to write, and what stayed.
/// </summary>
/// <param name="Overlay">The whole overlay, in its order; a moved entry is the definition its reference reads back as.</param>
/// <param name="Databases">Each database that changed, by file name, as it should be written.</param>
/// <param name="Moved">The ids moved, with the file each went into.</param>
/// <param name="Stayed">Signals that might have been expected to move and did not, with the reason.</param>
public sealed record DbcMoveResult(
    IReadOnlyList<SignalDefinition> Overlay,
    IReadOnlyDictionary<string, DbcDocument> Databases,
    IReadOnlyList<(string Id, string File)> Moved,
    IReadOnlyList<DbcStay> Stayed)
{
    /// <summary>True when there is anything to write.</summary>
    public bool HasMoves => Moved.Count > 0;
}

/// <summary>
/// Moving the signals the person has established on their truck out of the catalog and into their
/// own CAN databases (ADR-0078): where and how into <c>truck-hs.dbc</c> and <c>truck-ms.dbc</c>, the
/// use left in the catalog as a reference (ADR-0074).
/// </summary>
/// <remarks>
/// <para>
/// <b>What moves.</b> An entry of the person's with a request — mode 01 or 22 — that they have
/// confirmed. An unconfirmed one stays until TEST has answered it, so the database holds only
/// measurements. An entry that only renames, hides or re-rates a shipped signal stays too: the
/// standard set is not the person's to move (ADR-0073, stage 4). A placeholder has nothing to move,
/// and an entry that already refers has moved.
/// </para>
/// <para>
/// <b>Nothing changes for the dash.</b> Each moved entry is read back through the database it went
/// into, as the next launch will read it, and kept only if the request, decode, unit and states come
/// back exactly as they were. Anything that would not stays where it is, with the reason.
/// </para>
/// <para>Pure: it reads databases through <c>open</c> and writes nothing.</para>
/// </remarks>
public static class DbcMove
{
    /// <summary>The database for each bus, in the person's <c>dbc\</c> folder.</summary>
    public static string FileFor(CanBus bus) => bus is CanBus.Ms ? "truck-ms.dbc" : "truck-hs.dbc";

    /// <summary>Work out the move.</summary>
    /// <param name="overlay">The person's own definitions, as loaded.</param>
    /// <param name="shipped">The catalog as it ships, to tell a correction from a rename.</param>
    /// <param name="open">A database already in the folder by file name, or null when there is none yet.</param>
    public static DbcMoveResult Plan(IReadOnlyList<SignalDefinition> overlay, SignalCatalog shipped, Func<string, DbcDocument?> open)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(open);

        var databases = new Dictionary<string, DbcDocument>(StringComparer.OrdinalIgnoreCase);
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SignalDefinition>(overlay.Count);
        var moved = new List<(string, string)>();
        var stayed = new List<DbcStay>();

        foreach (var d in overlay)
        {
            if (!IsCandidate(d, shipped))
            {
                result.Add(d);
                continue;
            }

            if (Why(d) is { } why)
            {
                stayed.Add(new DbcStay(d.Id, why));
                result.Add(d);
                continue;
            }

            var file = FileFor(d.Bus);

            if (!databases.TryGetValue(file, out var database) && !refused.ContainsKey(file))
            {
                (database, var problem) = Prepare(file, d.Bus, open);

                if (database is null)
                {
                    refused[file] = problem!;
                }
                else
                {
                    databases[file] = database;
                }
            }

            if (database is null)
            {
                stayed.Add(new DbcStay(d.Id, refused[file]));
                result.Add(d);
                continue;
            }

            var (after, readBack, refusal) = Put(database, file, d);

            if (refusal is not null)
            {
                stayed.Add(new DbcStay(d.Id, refusal));
                result.Add(d);
                continue;
            }

            databases[file] = after!;
            changed.Add(file);
            moved.Add((d.Id, file));
            result.Add(readBack!);
        }

        return new DbcMoveResult(
            result,
            databases.Where(p => changed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            moved,
            stayed);
    }

    /// <summary>
    /// True for an entry the person might expect to move: one of theirs that asks for something,
    /// is not already a reference, and says something about where or how the shipped catalog does not.
    /// </summary>
    private static bool IsCandidate(SignalDefinition d, SignalCatalog shipped) =>
        d.Dbc is null
        && d.HasRequest
        && !(shipped.TryGet(d.Id, out var original) && original.HasRequest && SameWhereAndHow(original, d));

    /// <summary>Why a candidate cannot move at all, before any database is touched.</summary>
    private static string? Why(SignalDefinition d)
    {
        if (d.Unconfirmed)
        {
            return "unconfirmed — TEST it on the truck and save it first";
        }

        if (d.Kind is not SignalSourceKind.ObdPid || !DiagnosticAnswers.IsSupportedMode(d.Mode))
        {
            return Invariant($"mode {d.Mode:X2} — a database holds answers to modes 01 and 22 only");
        }

        if (d.Mode == 0x01 && d.Pid > 0xFF)
        {
            return "a mode 01 PID is one byte";
        }

        if (d.Module is not null && d.ModuleAddress is null)
        {
            return $"module '{d.Module}' is not a module address";
        }

        if (d.Module is null && d.Mode != 0x01)
        {
            return "asked of every module at once, which a database cannot say — give it the module that answers";
        }

        return null;
    }

    /// <summary>The database a bus's signals go into: the one there, or a new one saying its bus.</summary>
    private static (DbcDocument?, string?) Prepare(string file, CanBus bus, Func<string, DbcDocument?> open)
    {
        if (open(file) is not { } existing)
        {
            return (DiagnosticAnswers.WithBus(DbcDocument.Empty, bus), null);
        }

        return DiagnosticAnswers.BusOf(existing) switch
        {
            { } said when said == bus => (existing, null),
            { } other => (null, $"{file} is there already and says it is {Name(other)}, not {Name(bus)}"),
            null => (null, $"{file} is there already and does not say which bus it is (the {DiagnosticAnswers.BusAttribute} attribute)"),
        };
    }

    /// <summary>One signal into a database, read back; or why not.</summary>
    private static (DbcDocument?, SignalDefinition?, string?) Put(DbcDocument database, string file, SignalDefinition d)
    {
        var module = d.ModuleAddress ?? 0x7E0;
        var message = database.Messages.FirstOrDefault(m => !m.IsExtended && m.Id == DiagnosticAnswers.AnswerId(module));
        var name = NameFor(d, message);

        if (DiagnosticAnswers.Layout(d.Decode, d.Mode, name, d.Min ?? 0, d.Max ?? 0) is not { } layout)
        {
            return Refuse("its decode has no field a database can describe — a mask that is not one run of bits, signed and masked, or three bytes");
        }

        var values = new List<DbcValueText>();

        foreach (var state in d.States ?? [])
        {
            var raw = (state.Value - layout.Offset) / layout.Scale;

            if (double.IsNaN(raw) || raw != Math.Round(raw) || Math.Abs(raw) > long.MaxValue / 2)
            {
                return Refuse(Invariant($"its state '{state.Name}' ({state.Value}) is not a whole step of the value"));
            }

            values.Add(new DbcValueText((long)raw, state.Name));
        }

        layout = layout with { Comment = d.Name, Values = values };

        DbcDocument after;

        try
        {
            after = DiagnosticAnswers.Put(database, module, d.Mode, d.Pid, layout);
        }
        catch (InvalidOperationException ex)
        {
            return Refuse($"{file}: {ex.Message}");
        }

        // The module's message keeps the name it had, in a database that already held it.
        var written = after.Messages.First(m => !m.IsExtended && m.Id == DiagnosticAnswers.AnswerId(module));
        var reference = new DbcReference(file, written.Name, name);

        var asWritten = d with
        {
            Dbc = reference,
            Decode = null!,
            Pid = 0,
            Module = null,
            States = null,
            Placeholder = false,
            DbcProblem = null,
            DbcResolved = false,
        };

        // Read back from the text the file will hold, not from the document in hand: that is what the
        // next launch opens.
        var (onDisk, _) = DbcReader.Read(DbcWriter.Write(after));
        var readBack = DbcReferences.Resolve([asWritten], DbcLibrary.Of(new Dictionary<string, DbcDocument> { [file] = onDisk }))[0];

        if (readBack.DbcProblem is { } problem)
        {
            return Refuse($"it would not read back: {problem}");
        }

        if (Difference(d, readBack) is { } difference)
        {
            return Refuse($"it would read back with a different {difference}");
        }

        return (after, readBack, null);

        static (DbcDocument?, SignalDefinition?, string?) Refuse(string why) => (null, null, why);
    }

    /// <summary>
    /// The signal's name in the database: its id with anything a DBC name cannot hold made an
    /// underscore, as the example database names them. A name already used in the module's message
    /// for a different value gets a number.
    /// </summary>
    internal static string NameFor(SignalDefinition d, DbcMessage? message)
    {
        var text = new StringBuilder(d.Id.Length);

        foreach (var c in d.Id)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (text.Length == 0 || char.IsAsciiDigit(text[0]))
        {
            text.Insert(0, "S_");
        }

        var name = text.ToString();

        if (name is DiagnosticAnswers.ServiceSignal or DiagnosticAnswers.PidSignal or DiagnosticAnswers.IdentifierSignal or DiagnosticAnswers.LengthSignal)
        {
            name += "_";
        }

        var candidate = name;

        for (var n = 2; message?.Signal(candidate) is { } taken && !SamePlace(taken, d); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name}_{n}");
        }

        return candidate;
    }

    /// <summary>A signal already in the message at the same request: putting it again replaces it.</summary>
    private static bool SamePlace(DbcSignal signal, SignalDefinition d) =>
        signal.Switch is { Ranges: [{ IsSingle: true } range] } hangs
        && range.From == d.Pid
        && hangs.Multiplexor == (d.Mode == 0x01 ? DiagnosticAnswers.PidSignal : DiagnosticAnswers.IdentifierSignal);

    /// <summary>What a definition read back through its database differs in, or null when it reads the same.</summary>
    private static string? Difference(SignalDefinition before, SignalDefinition after) =>
        before.Bus != after.Bus ? "bus"
        : before.Kind != after.Kind || before.Mode != after.Mode || before.Pid != after.Pid ? "request"
        : before.ModuleAddress != after.ModuleAddress ? "module"
        : before.Decode != after.Decode ? "decode"
        : !(before.States ?? []).SequenceEqual(after.States ?? []) ? "set of states"
        : null;

    private static bool SameWhereAndHow(SignalDefinition a, SignalDefinition b) => Difference(a, b) is null;

    private static string Name(CanBus bus) => bus is CanBus.Ms ? "MS-CAN" : "HS-CAN";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
