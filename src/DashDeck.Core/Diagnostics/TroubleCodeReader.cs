using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Vehicle;

namespace DashDeck.Core.Diagnostics;

/// <summary>One code, the list it is on, and the module that reported it.</summary>
public sealed record ReportedCode(TroubleCode Code, TroubleCodeKind Kind, ushort? Module, string ModuleName)
{
    public string Text => Code.Text;

    public bool IsPending => Kind == TroubleCodeKind.Pending;
}

/// <summary>What a read of the trouble codes found.</summary>
/// <param name="Codes">Every code, stored first, each once per module.</param>
/// <param name="Answered">The modules that answered — none means nothing was heard at all.</param>
/// <param name="ReadAt">When the read finished.</param>
public sealed record TroubleCodeReport(IReadOnlyList<ReportedCode> Codes, IReadOnlyList<string> Answered, DateTimeOffset ReadAt)
{
    public IEnumerable<ReportedCode> Stored => Codes.Where(c => c.Kind == TroubleCodeKind.Stored);

    public IEnumerable<ReportedCode> Pending => Codes.Where(c => c.Kind == TroubleCodeKind.Pending);

    /// <summary>True when at least one module answered, so "no codes" is an answer rather than silence.</summary>
    public bool AnyAnswered => Answered.Count > 0;

    /// <summary>A line for a log or a screen: <c>stored P0420 (7E0); pending none</c>.</summary>
    public string Summary
    {
        get
        {
            static string List(IEnumerable<ReportedCode> codes) =>
                codes.Any() ? string.Join(" ", codes.Select(c => c.Module is { } m ? string.Create(CultureInfo.InvariantCulture, $"{c.Text}({m:X3})") : c.Text)) : "none";

            return AnyAnswered ? $"stored {List(Stored)}; pending {List(Pending)}" : "no module answered";
        }
    }
}

/// <summary>
/// Reads the stored (mode 03) and pending (mode 07) trouble codes from the emissions modules
/// (ADR-0055). Reads only.
/// </summary>
/// <remarks>
/// Each emissions module is asked by its address rather than by the broadcast, because the parser
/// keeps the first of several broadcast answers (ADR-0035), and a code in the transmission computer
/// would be lost behind the engine computer's answer. Which addresses are emissions modules is
/// ISO 15765-4's (<c>7E0</c>–<c>7E7</c>), named in the reference file and the user's vehicle file;
/// with none named, the broadcast is asked.
/// </remarks>
public static class TroubleCodeReader
{
    /// <summary>ISO 15765-4's emissions modules: <c>7E0</c> to <c>7E7</c>.</summary>
    public static bool IsEmissionsModule(ushort address) => address is >= 0x7E0 and <= 0x7E7;

    public static async Task<TroubleCodeReport> ReadAsync(
        Func<PidRequest, CancellationToken, Task<PidResponse>> ask,
        IReadOnlyDictionary<ushort, string> moduleNames,
        IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(moduleNames);
        ArgumentNullException.ThrowIfNull(clock);

        var modules = moduleNames.Keys.Where(IsEmissionsModule).Order().Select(m => (ushort?)m).ToList();
        if (modules.Count == 0)
        {
            modules.Add(null);
        }

        var codes = new List<ReportedCode>();
        var answered = new List<string>();

        foreach (var module in modules)
        {
            var name = module is { } m
                ? moduleNames.TryGetValue(m, out var label) ? $"{m:X3} {label}" : m.ToString("X3", CultureInfo.InvariantCulture)
                : "broadcast";
            var heard = false;

            foreach (var (mode, kind) in new[] { (ObdService.StoredCodes, TroubleCodeKind.Stored), (ObdService.PendingCodes, TroubleCodeKind.Pending) })
            {
                var reply = await ask(PidRequest.Service(mode, CanBus.Hs, module), ct).ConfigureAwait(false);
                if (!reply.ModuleAnswered)
                {
                    continue;
                }

                heard = true;
                if (reply.IsSuccess)
                {
                    foreach (var code in TroubleCode.FromPayload(reply.Data))
                    {
                        if (!codes.Any(c => c.Code == code && c.Kind == kind && c.Module == module))
                        {
                            codes.Add(new ReportedCode(code, kind, module, name));
                        }
                    }
                }
            }

            if (heard)
            {
                answered.Add(name);
            }
        }

        return new TroubleCodeReport([.. codes.OrderBy(c => c.Kind)], answered, clock.UtcNow);
    }
}
