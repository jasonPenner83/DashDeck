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
    /// The emulator features to change for a GPU mode. On the GPU the emulator composes Android's
    /// layers itself (<c>HostComposition</c>), and on the Surface that composition ignored the screen
    /// Android was told to use (<see cref="AndroidFit"/>): the picture came out in a narrow strip,
    /// stretched and cut. Turned off, Android composes its own picture — still on the GPU, through
    /// the emulator's translator — and the emulator shows it as Android drew it.
    /// </summary>
    public static IReadOnlyList<string> Features(string? mode) =>
        mode is null || IsSoftware(mode) ? [] : ["-HostComposition"];

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

    /// <summary>
    /// How the running emulator draws, from its own configuration (<c>getStatus</c>'s platform
    /// config): <c>hw.gpu.mode</c> as it resolved it — <c>host</c>, or a software mode marked slow —
    /// or null when it does not say.
    /// </summary>
    public static string? FromConfig(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.GetValueOrDefault("hw.gpu.enabled") is "no" or "false")
        {
            return "no GPU (software — slow)";
        }

        return config.GetValueOrDefault("hw.gpu.mode") is { Length: > 0 } mode
            ? IsSoftware(mode) ? mode + " (software — slow)" : mode
            : null;
    }

    /// <summary>
    /// The shell command that asks Android what draws it: SurfaceFlinger's <c>GLES:</c> line, which
    /// names the GPU behind the emulator's translator — however the emulator was started, and even
    /// when it said nothing about it.
    /// </summary>
    /// <remarks>The colon matters: the dump has a <c>RE GLES</c> heading before the line itself.</remarks>
    public const string GlesProbe = "dumpsys SurfaceFlinger | grep -m 1 'GLES:'";

    /// <summary>
    /// What draws Android, from <see cref="GlesProbe"/>'s answer — the GPU's own name, or the software
    /// renderer's marked as slow — or null when Android has not said yet.
    /// </summary>
    /// <example>
    /// <c>GLES: Google (Intel), Android Emulator OpenGL ES Translator (Intel(R) Iris(R) Plus Graphics), OpenGL ES 3.0 (…)</c>
    /// is <c>Intel(R) Iris(R) Plus Graphics</c>.
    /// </example>
    public static string? FromGles(string? output)
    {
        var line = (output ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("GLES:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var said = line["GLES:".Length..].Trim();

        // The translator names the GPU in brackets of its own, which hold brackets — Intel(R) — so
        // they are counted, not matched.
        const string translator = "Translator (";
        var at = said.IndexOf(translator, StringComparison.Ordinal);
        if (at >= 0)
        {
            var start = at + translator.Length;
            var depth = 1;
            var end = start;
            for (; end < said.Length && depth > 0; end++)
            {
                depth += said[end] switch { '(' => 1, ')' => -1, _ => 0 };
            }

            said = said[start..(depth == 0 ? end - 1 : end)].Trim();
        }

        string[] software = ["SwiftShader", "llvmpipe", "lavapipe", "Software"];
        return software.Any(s => said.Contains(s, StringComparison.OrdinalIgnoreCase)) ? said + " (software — slow)" : said;
    }

    /// <summary>
    /// The one shell command that asks Android what draws it and what it believes its screen is:
    /// SurfaceFlinger's <c>GLES:</c> line, <c>wm size</c>, <c>wm density</c> and its processor count.
    /// </summary>
    public const string AndroidProbe = GlesProbe + "; wm size; wm density; nproc";

    /// <summary>
    /// What Android said of its screen in <see cref="AndroidProbe"/>'s answer, in one line —
    /// <c>screen 1824x1272 → 1543x1272 · 223 dpi · 6 cpus</c> — or null when it said none of it.
    /// For when the picture looks wrong: it tells the emulator's screen, what Android was told to use
    /// of it, and how many processors Android really has.
    /// </summary>
    public static string? ScreenReport(string? output)
    {
        string? physical = null, forced = null, density = null, cpus = null;

        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Physical size:", StringComparison.Ordinal))
            {
                physical = line["Physical size:".Length..].Trim();
            }
            else if (line.StartsWith("Override size:", StringComparison.Ordinal))
            {
                forced = line["Override size:".Length..].Trim();
            }
            else if (line.StartsWith("Override density:", StringComparison.Ordinal) || (density is null && line.StartsWith("Physical density:", StringComparison.Ordinal)))
            {
                density = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
            else if (line.Length is > 0 and <= 3 && line.All(char.IsAsciiDigit))
            {
                cpus = line;
            }
        }

        if (physical is null && density is null && cpus is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (physical is not null)
        {
            parts.Add(forced is null ? $"screen {physical}" : $"screen {physical} → {forced}");
        }

        if (density is not null)
        {
            parts.Add($"{density} dpi");
        }

        if (cpus is not null)
        {
            parts.Add($"{cpus} cpus");
        }

        return string.Join(" · ", parts);
    }

    [GeneratedRegex(@"using '([a-z_]+)' mode", RegexOptions.IgnoreCase)]
    private static partial Regex ModeLine();

    // "OpenGL Renderer=[Android Emulator OpenGL ES Translator (Intel(R) Iris(R) Plus Graphics)]"
    [GeneratedRegex(@"OpenGL Renderer=\[(?:Android Emulator OpenGL ES Translator \()?([^\]]+?)\)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex RendererLine();
}

/// <summary>
/// How many things happened a second, lately — frames received, frames drawn — over a short window
/// that slides with the clock it is given. Safe to add to from one thread and read from another.
/// </summary>
public sealed class RollingRate(TimeSpan window)
{
    private readonly Queue<TimeSpan> _times = new();

    /// <summary>Something happened at this moment (a monotonic clock's elapsed time).</summary>
    public void Add(TimeSpan at)
    {
        lock (_times)
        {
            _times.Enqueue(at);
            Trim(at);
        }
    }

    /// <summary>How many a second over the window ending now.</summary>
    public double PerSecond(TimeSpan now)
    {
        lock (_times)
        {
            Trim(now);
            return _times.Count / window.TotalSeconds;
        }
    }

    /// <summary>
    /// The most that happened in any one second of the window ending now — the frame rate while
    /// something moved, which is what matters: the emulator sends a frame only when the picture
    /// changes, so a still screen reads nothing, and the menu that shows this is opened with Android
    /// standing still.
    /// </summary>
    public int BusiestSecond(TimeSpan now)
    {
        lock (_times)
        {
            Trim(now);

            var times = _times.ToArray();
            var busiest = 0;
            for (int first = 0, last = 0; last < times.Length; last++)
            {
                while (times[last] - times[first] >= TimeSpan.FromSeconds(1))
                {
                    first++;
                }

                busiest = Math.Max(busiest, last - first + 1);
            }

            return busiest;
        }
    }

    private void Trim(TimeSpan now)
    {
        while (_times.Count > 0 && now - _times.Peek() > window)
        {
            _times.Dequeue();
        }
    }
}

/// <summary>Android's own settings that make it feel quicker (ADR-0075 addendum).</summary>
public static class AndroidSpeed
{
    /// <summary>
    /// Android's three animation scales at half: windows, transitions and animators finish in half the
    /// time, so a screen that draws at a modest rate still answers a tap promptly. Android's own
    /// developer setting; <c>1</c> puts it back.
    /// </summary>
    public const string HalfAnimations =
        "settings put global window_animation_scale 0.5; settings put global transition_animation_scale 0.5; settings put global animator_duration_scale 0.5";
}

/// <summary>
/// How big a screen Android draws, against the stage's own pixels (ADR-0075 addendum). On the Surface
/// the processor is the limit — every frame is read back, packed, sent, unpacked, converted and copied
/// — and all of it scales with the pixels. A smaller screen is scaled up to fill the space: softer,
/// and quicker. Android's units stay the dash's (<see cref="AndroidFit"/>), so nothing gets smaller.
/// </summary>
public static class AndroidPicture
{
    /// <summary>The sizes, in order, with the hold menu's names for them.</summary>
    public static readonly IReadOnlyList<(string Name, double Scale)> Sizes = [("SHARP", 1.0), ("BALANCED", 0.75), ("FAST", 0.5)];

    /// <summary>A size from the settings file; BALANCED when it is not one.</summary>
    public static (string Name, double Scale) Parse(string? saved) =>
        Sizes.FirstOrDefault(s => string.Equals(s.Name, saved?.Trim(), StringComparison.OrdinalIgnoreCase)) is { Name: not null } size
            ? size
            : Sizes[1];

    /// <summary>The size after this one, round again after FAST.</summary>
    public static (string Name, double Scale) Next((string Name, double Scale) size)
    {
        var at = Sizes.ToList().FindIndex(s => s.Name == size.Name);
        return Sizes[(at + 1) % Sizes.Count];
    }

    /// <summary>
    /// The emulator's <c>-skin</c> for a size: the stage's own pixels scaled, even on both sides, and
    /// never under the 320 the emulator takes.
    /// </summary>
    public static string Skin(int stageWidth, int stageHeight, double scale)
    {
        static int Side(int pixels, double scale) => Math.Max(320, (int)Math.Round(pixels * scale / 2) * 2);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Side(stageWidth, scale)}x{Side(stageHeight, scale)}");
    }
}
