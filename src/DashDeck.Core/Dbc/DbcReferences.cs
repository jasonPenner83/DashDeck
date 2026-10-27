using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.Core.Dbc;

/// <summary>
/// Following a catalog entry's <see cref="SignalDefinition.Dbc"/> into the database it names (ADR-0074).
/// </summary>
/// <remarks>
/// The entry says how the dash uses a value; the database says where the value is. Resolving puts
/// the two together into the same <see cref="SignalDefinition"/> a wholly-written entry is, so
/// nothing past the loader — polling, arbitration, cards, warnings, the synthetic truck — can tell
/// them apart.
/// <para>
/// <b>Two kinds of failure, treated differently.</b> An entry that is written wrong — a reference
/// that also gives its own decode, an entry with neither — is refused like any other invalid
/// definition, because that is a mistake in the file in front of the person. A reference the
/// database cannot answer — the file moved, a signal renamed, a layout the catalog cannot decode —
/// loads as a <see cref="SignalDefinition.Placeholder"/> carrying the reason: nothing is asked for it,
/// a real vehicle reads Unavailable, and every other signal keeps working.
/// </para>
/// </remarks>
public static class DbcReferences
{
    /// <summary>
    /// The JSON names a reference leaves to its database, so writing one back puts none of them in
    /// the catalog (<see cref="SignalCatalog.ToJson"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> DatabaseFields =
        new HashSet<string>(["bus", "kind", "mode", "pid", "module", "decode", "states", "placeholder"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every definition with its reference followed; the rest as they are.</summary>
    /// <exception cref="InvalidDataException">An entry is written wrong — see the remarks.</exception>
    public static IReadOnlyList<SignalDefinition> Resolve(IEnumerable<SignalDefinition> definitions, DbcLibrary databases)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(databases);

        var list = definitions.ToList();
        var problems = list.SelectMany(AsWritten).ToList();

        if (problems.Count > 0)
        {
            throw new InvalidDataException(string.Join("; ", problems));
        }

        return [.. list.Select(d => d.Dbc is null || d.DbcResolved ? d : Follow(d, databases))];
    }

    /// <summary>
    /// The reference an edited definition keeps: the one it had, while the edit left alone what the
    /// database decides; none once the edit changed any of it, so the change is saved in the
    /// catalog rather than quietly dropped for the database's.
    /// </summary>
    public static DbcReference? Keep(SignalDefinition before, SignalDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.Dbc is { } reference && before.DbcProblem is null && SameAsDatabase(before, after) ? reference : null;
    }

    private static bool SameAsDatabase(SignalDefinition a, SignalDefinition b) =>
        a.Bus == b.Bus && a.Mode == b.Mode && a.Pid == b.Pid && a.ModuleAddress == b.ModuleAddress
        && a.Decode == b.Decode
        && (a.States ?? []).SequenceEqual(b.States ?? []);

    private static IEnumerable<string> AsWritten(SignalDefinition d)
    {
        // Decode is declared required, but a reference leaves it out, so the reader does not insist.
        var decode = (DecodeSpec?)d.Decode;

        if (d.DbcResolved)
        {
            yield break;
        }

        if (d.Dbc is not { } reference)
        {
            if (decode is null)
            {
                yield return $"'{d.Id}': decode is missing — give one, or a \"dbc\" reference to a database";
            }

            yield break;
        }

        if (!DbcLibrary.IsPlainName(reference.File) || string.IsNullOrWhiteSpace(reference.Message) || string.IsNullOrWhiteSpace(reference.Signal))
        {
            yield return $"'{d.Id}': dbc needs a file name (no folder), a message and a signal";
        }

        if (decode is not null || d.Pid != 0 || d.Module is not null || d.States is not null || d.Placeholder)
        {
            yield return $"'{d.Id}': a \"dbc\" reference takes its pid, module, decode and states from the database — leave them out";
        }
    }

    private static SignalDefinition Follow(SignalDefinition d, DbcLibrary databases)
    {
        var reference = d.Dbc!;
        var (document, problem) = databases.Open(reference.File);

        if (document is null)
        {
            return Unresolved(d, problem!);
        }

        if (document.Messages.FirstOrDefault(m => m.Name == reference.Message) is not { } message)
        {
            return Unresolved(d, $"{reference.File} has no message {reference.Message}");
        }

        if (message.Signal(reference.Signal) is null)
        {
            return Unresolved(d, $"{reference.Message} in {reference.File} has no signal {reference.Signal}");
        }

        var answers = DiagnosticAnswers.Read(document);
        var answer = answers.Answers.FirstOrDefault(a => a.Message.Name == reference.Message && a.Signal.Name == reference.Signal);

        if (answer is null)
        {
            var why = answers.Problems.FirstOrDefault(p => p.StartsWith($"{reference.Message}.{reference.Signal}:", StringComparison.Ordinal));
            return Unresolved(d, why ?? $"{reference.Message}.{reference.Signal} is not an answer to mode 01 or 22, which is all the dash asks");
        }

        if (DiagnosticAnswers.BusOf(document) is not { } bus)
        {
            return Unresolved(d, $"{reference.File} does not say which bus it is (the {DiagnosticAnswers.BusAttribute} attribute)");
        }

        if (answer.ToDecodeSpec() is not { } decode)
        {
            return Unresolved(d, $"{reference.Message}.{reference.Signal} is laid out in a way the catalog cannot decode yet (whole bytes, 1, 2 or 4 of them, read big-endian)");
        }

        var signal = answer.Signal;
        var states = signal.Values.Count > 0
            ? signal.Values.Select(v => new SignalState((v.Raw * signal.Scale) + signal.Offset, v.Text)).ToList()
            : null;

        return d with
        {
            Bus = bus,
            Kind = SignalSourceKind.ObdPid,
            Mode = answer.Mode,
            Pid = answer.Pid,

            // Mode 01 to the engine is asked of the broadcast, as every standard PID is; anything else
            // is asked of its own module by address (ADR-0035).
            Module = answer.Mode == 0x01 && answer.Module == 0x7E0 ? null : answer.Module.ToString("X3", CultureInfo.InvariantCulture),
            Decode = decode,
            States = states,
            Placeholder = false,
            DbcProblem = null,
            DbcResolved = true,
        };
    }

    private static SignalDefinition Unresolved(SignalDefinition d, string problem) => d with
    {
        Bus = CanBus.Hs,
        Mode = 0x01,
        Pid = 0,
        Module = null,
        Decode = new DecodeSpec(0, 1, false, 1, 0, ""),
        States = null,
        Placeholder = true,
        DbcProblem = problem,
        DbcResolved = true,
    };
}
