using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DashDeck.Core.Emulation;

/// <summary>
/// Which DNS servers Android is given (ADR-0075 addendum). Left to itself, the emulator on Windows
/// can pick a virtual adapter's — the hypervisor's own switch, which answers nothing — and Android
/// says <i>Connected / No internet access</i>, as it did on the Surface.
/// </summary>
public static partial class EmulatorNetwork
{
    /// <summary>
    /// Public resolvers after the tablet's own, so a name still resolves when the truck moves from the
    /// house's Wi-Fi to the phone's hotspot while Android runs — the emulator's DNS is fixed at start.
    /// </summary>
    public static readonly IReadOnlyList<string> Public = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// The servers to pass with <c>-dns-server</c>: up to two of the tablet's own — from the adapter
    /// that reaches the internet — then the public ones; four at most, which is all the emulator takes.
    /// IPv4 only, and never loopback, link-local or unset.
    /// </summary>
    public static IReadOnlyList<string> DnsServers(IEnumerable<string> tabletServers)
    {
        ArgumentNullException.ThrowIfNull(tabletServers);

        var own = tabletServers
            .Select(s => IPAddress.TryParse(s?.Trim(), out var address) ? address : null)
            .Where(a => a is { AddressFamily: AddressFamily.InterNetwork } && !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any)
                && !(a.GetAddressBytes() is [169, 254, ..]))
            .Select(a => a!.ToString())
            .Distinct()
            .Take(2);

        return [.. own.Concat(Public).Distinct().Take(4)];
    }

    /// <summary>True for a list the emulator takes: one to four IPv4 addresses, comma-separated.</summary>
    public static bool IsServerList(string text) =>
        text?.Split(',') is { Length: >= 1 and <= 4 } parts
        && parts.All(p => IPAddress.TryParse(p, out var a) && a.AddressFamily == AddressFamily.InterNetwork);
}

/// <summary>
/// The <c>-gpu</c> mode to start with, and whether to start again with the emulator's own choice if
/// the emulator will not start with it.
/// </summary>
public sealed record GpuChoice(string? Mode, bool FallBack)
{
    /// <summary>The emulator's own choice, and nothing to fall back from.</summary>
    public static readonly GpuChoice Emulator = new(null, false);
}

/// <summary>
/// How the emulator draws (ADR-0075 addendum): its <c>-gpu</c> mode, and what it said it chose. Drawn
/// in software Android crawls; on the tablet's own GPU it does not.
/// </summary>
/// <remarks>
/// Left to itself the emulator drew in software on the Surface (<i>drawn by sw</i>): it does not trust
/// the Intel Iris Plus driver, whose Vulkan is older than it wants. So the dash asks for the host's
/// GPU through its OpenGL driver, and only if the emulator will not start that way does it leave the
/// choice to the emulator.
/// </remarks>
public static partial class EmulatorGpu
{
    /// <summary>The modes the emulator takes with <c>-gpu</c>.</summary>
    public static readonly IReadOnlyList<string> Modes = ["auto", "host", "angle_indirect", "swiftshader_indirect", "guest"];

    /// <summary>
    /// What the settings file's <c>androidGpu</c> asks for. <c>auto</c> — and anything not understood
    /// — is the dash's choice: the host's GPU, falling back to the emulator's own. <c>emulator</c>
    /// leaves it to the emulator. Any of the emulator's own modes is used as it is, with no fallback.
    /// </summary>
    public static GpuChoice Choose(string? saved)
    {
        var asked = saved?.Trim();

        if (string.Equals(asked, "emulator", StringComparison.OrdinalIgnoreCase))
        {
            return GpuChoice.Emulator;
        }

        return Modes.FirstOrDefault(m => string.Equals(m, asked, StringComparison.OrdinalIgnoreCase)) is { } mode && mode != "auto"
            ? new GpuChoice(mode, false)
            : new GpuChoice("host", true);
    }

    /// <summary>
    /// True for a mode that draws without the GPU. The emulator has several names for it —
    /// <c>swiftshader_indirect</c>, <c>swangle_indirect</c>, and lately just <c>sw</c>.
    /// </summary>
    public static bool IsSoftware(string? mode) =>
        mode is not null
        && (mode.StartsWith("sw", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("swiftshader", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("software", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("lavapipe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the emulator said about how it draws, from its own output: the mode, and the GPU behind
    /// it when it named one. Null when it has said nothing yet.
    /// </summary>
    public static string? Renderer(IEnumerable<string> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? mode = null, gpu = null;
        var software = false;

        foreach (var line in output)
        {
            if (ModeLine().Match(line) is { Success: true } m)
            {
                mode = m.Groups[1].Value;
            }

            if (RendererLine().Match(line) is { Success: true } r)
            {
                gpu = r.Groups[1].Value.Trim();
            }

            software |= line.Contains("swiftshader", StringComparison.OrdinalIgnoreCase) || line.Contains("swangle", StringComparison.OrdinalIgnoreCase);
        }

        mode ??= software ? "swiftshader" : null;
        if (mode is null && gpu is null)
        {
            return null;
        }

        var said = mode is null ? gpu! : gpu is null ? mode : $"{mode} · {gpu}";
        return IsSoftware(mode) ? said + " (software — slow)" : said;
    }

    [GeneratedRegex(@"using '([a-z_]+)' mode", RegexOptions.IgnoreCase)]
    private static partial Regex ModeLine();

    // "OpenGL Renderer=[Android Emulator OpenGL ES Translator (Intel(R) Iris(R) Plus Graphics)]"
    [GeneratedRegex(@"OpenGL Renderer=\[(?:Android Emulator OpenGL ES Translator \()?([^\]]+?)\)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex RendererLine();
}
