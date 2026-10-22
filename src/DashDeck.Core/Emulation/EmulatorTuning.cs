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
/// How the emulator draws (ADR-0075 addendum): its <c>-gpu</c> mode, and what it said it chose. Drawn
/// in software (SwiftShader) Android crawls; on the tablet's own GPU it does not.
/// </summary>
public static partial class EmulatorGpu
{
    /// <summary>The modes the emulator takes; <c>auto</c> is its own choice and the default.</summary>
    public static readonly IReadOnlyList<string> Modes = ["auto", "host", "angle_indirect", "swiftshader_indirect", "guest"];

    /// <summary>A mode from the settings file, or null — the emulator's own choice — when it is not one.</summary>
    public static string? Parse(string? saved) =>
        Modes.FirstOrDefault(m => string.Equals(m, saved?.Trim(), StringComparison.OrdinalIgnoreCase)) is { } mode && mode != "auto" ? mode : null;

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

            software |= line.Contains("swiftshader", StringComparison.OrdinalIgnoreCase);
        }

        mode ??= software ? "swiftshader" : null;
        if (mode is null && gpu is null)
        {
            return null;
        }

        var said = mode is null ? gpu! : gpu is null ? mode : $"{mode} · {gpu}";
        return mode?.Contains("swiftshader", StringComparison.OrdinalIgnoreCase) == true ? said + " (software — slow)" : said;
    }

    [GeneratedRegex(@"using '([a-z_]+)' mode", RegexOptions.IgnoreCase)]
    private static partial Regex ModeLine();

    // "OpenGL Renderer=[Android Emulator OpenGL ES Translator (Intel(R) Iris(R) Plus Graphics)]"
    [GeneratedRegex(@"OpenGL Renderer=\[(?:Android Emulator OpenGL ES Translator \()?([^\]]+?)\)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex RendererLine();
}
