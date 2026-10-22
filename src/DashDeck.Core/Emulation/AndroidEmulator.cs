using System.Globalization;

namespace DashDeck.Core.Emulation;

/// <summary>Which kind of Android an AVD runs.</summary>
public enum AndroidFlavour
{
    /// <summary>Phone or tablet Android: every app, a UI not made for driving.</summary>
    Regular,

    /// <summary>Android Automotive: the car OS, a driving UI, car-approved apps only.</summary>
    Automotive,
}

/// <summary>
/// One Android Virtual Device as its <c>config.ini</c> describes it (ADR-0075).
/// </summary>
/// <param name="Name">What <c>emulator -list-avds</c> calls it.</param>
/// <param name="Tag">The system image's tag: <c>google_apis_playstore</c>, <c>android-automotive-playstore</c>…</param>
/// <param name="Abi">The image's processor: <c>x86_64</c> is the one that runs fast on an Intel tablet.</param>
public sealed record AvdInfo(string Name, string Tag, string Abi)
{
    /// <summary>The emulated screen in pixels, or 0 when the file does not say.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Dots per inch the guest thinks its screen has, or 0.</summary>
    public int Density { get; init; }

    /// <summary>Memory given to the guest, in MB, or 0.</summary>
    public int RamMb { get; init; }

    /// <summary>True when the image carries Google Play.</summary>
    public bool PlayStore { get; init; }

    /// <summary>Automotive when the image's tag says so; anything else is regular Android.</summary>
    public AndroidFlavour Flavour =>
        Tag.Contains("automotive", StringComparison.OrdinalIgnoreCase) ? AndroidFlavour.Automotive : AndroidFlavour.Regular;

    /// <summary>One line for a report: <c>Pixel_Tablet — regular, x86_64, 2560×1600 @320, 4096 MB, Play</c>.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Name} — {(Flavour == AndroidFlavour.Automotive ? "Automotive" : "regular")}, {Abi}, " +
        $"{(Width > 0 ? $"{Width}×{Height}" : "screen ?")}{(Density > 0 ? $" @{Density}" : "")}, " +
        $"{(RamMb > 0 ? $"{RamMb} MB" : "RAM ?")}{(PlayStore ? ", Play" : "")} [{Tag}]");

    /// <summary>
    /// Read an AVD's <c>config.ini</c>. Unknown or unreadable lines are ignored: the file is the
    /// emulator's, it grows keys every release, and nothing here depends on more than these few.
    /// </summary>
    public static AvdInfo Parse(string name, string configIni)
    {
        ArgumentNullException.ThrowIfNull(configIni);

        var keys = KeyValues.Parse(configIni);

        return new AvdInfo(name, keys.GetValueOrDefault("tag.id", ""), keys.GetValueOrDefault("abi.type", ""))
        {
            Width = Int(keys, "hw.lcd.width"),
            Height = Int(keys, "hw.lcd.height"),
            Density = Int(keys, "hw.lcd.density"),
            RamMb = ParseRam(keys.GetValueOrDefault("hw.ramSize", "")),
            PlayStore = string.Equals(keys.GetValueOrDefault("PlayStore.enabled", ""), "true", StringComparison.OrdinalIgnoreCase)
                || keys.GetValueOrDefault("tag.id", "").Contains("playstore", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static int Int(IReadOnlyDictionary<string, string> keys, string key) =>
        int.TryParse(keys.GetValueOrDefault(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // "4096", "4096M", "4096MB" and "4G" are all written by one tool or another.
    private static int ParseRam(string text)
    {
        var trimmed = text.Trim().ToUpperInvariant().TrimEnd('B');
        var factor = trimmed.EndsWith('G') ? 1024 : 1;
        trimmed = trimmed.TrimEnd('G', 'M');

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value * factor : 0;
    }
}

/// <summary>Where the Android SDK and its emulator are on this machine (ADR-0075).</summary>
/// <remarks>
/// DashDeck installs none of it: the person installs Android Studio, or the command-line tools,
/// and makes the AVDs. This only finds what is there — the folder named on the command line, then
/// the SDK's own environment variables, then Android Studio's default.
/// </remarks>
public static class AndroidSdk
{
    /// <summary>The folders to look in, most specific first, with duplicates and blanks removed.</summary>
    public static IReadOnlyList<string> Candidates(string? explicitPath, Func<string, string?> environment, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return
        [
            .. new[]
            {
                explicitPath,
                environment("ANDROID_HOME"),
                environment("ANDROID_SDK_ROOT"),
                string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Android", "Sdk"),
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>ADB inside an SDK folder — the SDK's own, not another on the PATH (scrcpy brings one).</summary>
    public static string AdbPath(string sdk) =>
        Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");

    /// <summary>The emulator program inside an SDK folder.</summary>
    public static string EmulatorPath(string sdk) =>
        Path.Combine(sdk, "emulator", OperatingSystem.IsWindows() ? "emulator.exe" : "emulator");

    /// <summary>The first candidate that has an emulator in it, or null.</summary>
    public static string? Find(IEnumerable<string> candidates, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        return candidates.FirstOrDefault(c => fileExists(EmulatorPath(c)));
    }

    /// <summary>
    /// Where AVDs live: <c>ANDROID_AVD_HOME</c>, else <c>ANDROID_USER_HOME\avd</c>, else
    /// <c>%USERPROFILE%\.android\avd</c> — the emulator's own order.
    /// </summary>
    public static string AvdHome(Func<string, string?> environment, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment("ANDROID_AVD_HOME") is { Length: > 0 } avdHome ? avdHome
            : environment("ANDROID_USER_HOME") is { Length: > 0 } userHome ? Path.Combine(userHome, "avd")
            : Path.Combine(userProfile, ".android", "avd");
    }
}

/// <summary>How DashDeck starts an emulator for the stage (ADR-0075).</summary>
public static class EmulatorCommand
{
    /// <summary>
    /// The arguments: the AVD, its window hidden, gRPC on a port with a token, no boot animation.
    /// </summary>
    /// <remarks>
    /// <b>Hidden, not windowless.</b> <c>-qt-hide-window</c> is how Android Studio runs the emulator
    /// it draws in its own panel: the window exists, so the GPU path is the ordinary one, and nobody
    /// sees it. <c>-no-window</c> is the alternative to try when that misbehaves.
    /// <para>
    /// <b>A token, always.</b> Without one, anything on the machine that finds the port can see the
    /// screen and type into it. <c>-grpc-use-token</c> writes the token where only this user can
    /// read it, and every call carries it.
    /// </para>
    /// </remarks>
    /// <param name="skin">
    /// A screen size for this run only, <c>1824x1272</c> — the stage — or null for the AVD's own. How a
    /// tablet image is tried at the stage's size without making another AVD.
    /// </param>
    /// <param name="memoryMb">Memory for the guest this run, or null for the AVD's own (<see cref="EmulatorResources"/>).</param>
    /// <param name="cores">Processor cores for the guest this run, or null for the AVD's own.</param>
    public static IReadOnlyList<string> Arguments(
        string avd, int grpcPort, bool noWindow = false, string? skin = null, int? memoryMb = null, int? cores = null,
        IReadOnlyList<string>? dnsServers = null, string? gpu = null, IReadOnlyList<string>? features = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avd);
        ArgumentOutOfRangeException.ThrowIfLessThan(grpcPort, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(grpcPort, 65535);

        if (skin is not null && !IsSize(skin))
        {
            throw new ArgumentException($"'{skin}' is not a size like 1824x1272", nameof(skin));
        }

        var dns = dnsServers is { Count: > 0 } ? string.Join(',', dnsServers) : null;
        if (dns is not null && !EmulatorNetwork.IsServerList(dns))
        {
            throw new ArgumentException($"'{dns}' is not one to four IPv4 addresses", nameof(dnsServers));
        }

        if (gpu is not null && !EmulatorGpu.Modes.Contains(gpu))
        {
            throw new ArgumentException($"'{gpu}' is not an emulator GPU mode", nameof(gpu));
        }

        if (features?.FirstOrDefault(f => !IsFeature(f)) is { } bad)
        {
            throw new ArgumentException($"'{bad}' is not an emulator feature", nameof(features));
        }

        return
        [
            "-avd", avd,
            noWindow ? "-no-window" : "-qt-hide-window",
            "-grpc", grpcPort.ToString(CultureInfo.InvariantCulture),
            "-grpc-use-token",
            "-no-boot-anim",
            .. skin is null ? Array.Empty<string>() : ["-skin", skin],
            .. memoryMb is { } memory ? ["-memory", memory.ToString(CultureInfo.InvariantCulture)] : Array.Empty<string>(),
            .. cores is { } count ? ["-cores", count.ToString(CultureInfo.InvariantCulture)] : Array.Empty<string>(),
            .. dns is null ? Array.Empty<string>() : ["-dns-server", dns],
            .. gpu is null ? Array.Empty<string>() : ["-gpu", gpu],
            .. (features ?? []).SelectMany(f => new[] { "-feature", f }),
        ];
    }

    /// <summary>A feature as <c>-feature</c> takes one: a name, turned off with a leading minus.</summary>
    private static bool IsFeature(string text) =>
        text is { Length: > 1 } && text.TrimStart('-').Length > 0 && text.TrimStart('-').All(char.IsAsciiLetterOrDigit) && !text.StartsWith("--", StringComparison.Ordinal);

    /// <summary>A screen size as the emulator takes one: <c>1824x1272</c>, each side 320–4096.</summary>
    public static bool IsSize(string text) =>
        text?.Split('x') is [var w, var h]
        && int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width is >= 320 and <= 4096
        && int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var height) && height is >= 320 and <= 4096;
}

/// <summary>Reading what ADB and the emulator say (ADR-0075).</summary>
public static class AdbAnswers
{
    /// <summary>
    /// True when <c>am start</c> really started the activity. ADB prints its own failures —
    /// <c>adb.exe: device offline</c> — without the word "error", which is how a failure once read
    /// as success on the Surface; so success is Android's own words, and nothing else is.
    /// </summary>
    public static bool Started(string output) =>
        !string.IsNullOrEmpty(output)
        && (output.Contains("Starting: Intent", StringComparison.Ordinal)
            || output.Contains("Activity not started, intent has been delivered", StringComparison.Ordinal))
        && !output.Contains("Error:", StringComparison.Ordinal);

    /// <summary>
    /// True when a shell command such as <c>wm size</c> was done: it exited cleanly and ADB printed no
    /// failure of its own — <c>adb.exe: device offline</c>, <c>error: device not found</c> — nor Android
    /// an exception. Those commands print nothing when they work.
    /// </summary>
    public static bool Done(int exitCode, string output) =>
        exitCode == 0
        && !(output ?? "").Contains("adb.exe:", StringComparison.Ordinal)
        && !(output ?? "").Contains("error:", StringComparison.OrdinalIgnoreCase)
        && !(output ?? "").Contains("Exception", StringComparison.Ordinal);

    /// <summary>What <c>adb get-state</c> said: <c>device</c> when it can be used, or why not.</summary>
    public static string State(string output)
    {
        var line = (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length == 0 ? "no answer" : line.Replace("adb.exe: ", "", StringComparison.Ordinal).Replace("error: ", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The different ADB programs the emulator ran, from its own warnings — the SDK's, and any other on
    /// the PATH. Two of different versions restart each other's server and leave the device offline.
    /// </summary>
    public static IReadOnlyList<string> Programs(IEnumerable<string> emulatorOutput)
    {
        ArgumentNullException.ThrowIfNull(emulatorOutput);

        const string marker = "adb command '";
        var programs = new List<string>();

        foreach (var line in emulatorOutput)
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var rest = line[(at + marker.Length)..];
            var end = rest.IndexOf(" -s ", StringComparison.Ordinal);
            var program = (end > 0 ? rest[..end] : rest).Trim();

            if (program.Length > 0 && !programs.Contains(program, StringComparer.OrdinalIgnoreCase))
            {
                programs.Add(program);
            }
        }

        return programs;
    }
}

/// <summary>
/// What a running emulator says about itself in its discovery file, <c>pid_&lt;pid&gt;.ini</c> (ADR-0075).
/// </summary>
/// <remarks>
/// The file's keys are not documented and have changed between releases, so this reads every
/// <c>key=value</c> line and names only the few the stage needs. The probe prints the rest, with
/// secrets hidden, so the format on the tablet is seen rather than assumed.
/// </remarks>
public sealed record EmulatorDiscovery(IReadOnlyDictionary<string, string> Keys)
{
    /// <summary>The gRPC port, or null when the file does not give one.</summary>
    public int? GrpcPort =>
        int.TryParse(Keys.GetValueOrDefault("grpc.port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null;

    /// <summary>The token every gRPC call must carry, or null.</summary>
    public string? GrpcToken => Keys.TryGetValue("grpc.token", out var token) && token.Length > 0 ? token : null;

    /// <summary>The AVD it is running, or null.</summary>
    public string? AvdName => Keys.GetValueOrDefault("avd.name");

    /// <summary>
    /// The name ADB knows it by — <c>emulator-5554</c>, from its console port — or null. ADB is how
    /// Automotive's vehicle data will be set (ADR-0075), and how the probe opens something to scroll.
    /// </summary>
    public string? AdbSerial =>
        int.TryParse(Keys.GetValueOrDefault("port.serial"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0
            ? string.Create(CultureInfo.InvariantCulture, $"emulator-{port}")
            : null;

    public static EmulatorDiscovery Parse(string text) => new(KeyValues.Parse(text));

    /// <summary>The file's lines for a report, with anything secret replaced by its length.</summary>
    public IEnumerable<string> Redacted() =>
        Keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => IsSecret(k.Key)
            ? string.Create(CultureInfo.InvariantCulture, $"{k.Key}=<{k.Value.Length} characters, hidden>")
            : $"{k.Key}={k.Value}");

    /// <summary>The folders a discovery file may be in, most likely first.</summary>
    public static IReadOnlyList<string> Folders(Func<string, string?> environment, string tempPath, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return
        [
            .. new[]
            {
                environment("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? Path.Combine(runtime, "avd", "running") : null,
                Path.Combine(tempPath, "avd", "running"),
                Path.Combine(userProfile, ".android", "avd", "running"),
            }
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The discovery file's name for a process.</summary>
    /// <remarks>
    /// The process is the virtual machine, <c>qemu-system-x86_64</c>, not <c>emulator.exe</c>, which
    /// only launches it — so the launcher's own id does not name the file. Found on the Surface.
    /// </remarks>
    public static string FileName(int pid) => string.Create(CultureInfo.InvariantCulture, $"pid_{pid}.ini");

    /// <summary>The process a discovery file is for, from its name — <c>pid_8280.ini</c> is 8280 — or null.</summary>
    public static int? PidOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? "");
        return name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(path ?? "").Equals(".ini", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? pid
                : null;
    }

    /// <summary>
    /// Where the emulator says it wrote its discovery file — its own line <c>Advertising in: …</c> —
    /// or null when it has not said yet. The most direct answer, so it is tried first.
    /// </summary>
    public static string? AdvertisedPath(IEnumerable<string> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        const string marker = "Advertising in:";

        foreach (var line in output)
        {
            var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && line[(at + marker.Length)..].Trim().Trim('"', '\'') is { Length: > 0 } path
                && path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// The newest discovery file written since the emulator was started, from those found — the
    /// fallback when it does not say where it wrote one. Null when none is new enough.
    /// </summary>
    public static string? Newest(IEnumerable<(string Path, DateTime WrittenUtc)> files, DateTime sinceUtc)
    {
        ArgumentNullException.ThrowIfNull(files);

        return files
            .Where(f => f.WrittenUtc >= sinceUtc && Path.GetFileName(f.Path).StartsWith("pid_", StringComparison.OrdinalIgnoreCase)
                && f.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.WrittenUtc)
            .Select(f => f.Path)
            .FirstOrDefault();
    }

    private static bool IsSecret(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("jwk", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Frames counted over a while, for a rate that says whether the stage can keep up.</summary>
public sealed class FrameMeter
{
    private readonly List<(TimeSpan At, int Bytes)> _frames = [];

    /// <summary>Record a frame that arrived at <paramref name="at"/>, measured from any fixed start.</summary>
    public void Add(TimeSpan at, int bytes) => _frames.Add((at, bytes));

    public int Count => _frames.Count;

    /// <summary>Frames a second between the first and the last, or 0 for fewer than two.</summary>
    public double FramesPerSecond =>
        _frames.Count < 2 ? 0 : (_frames.Count - 1) / Math.Max((_frames[^1].At - _frames[0].At).TotalSeconds, 1e-9);

    /// <summary>Megabytes a second over the same span.</summary>
    public double MegabytesPerSecond =>
        _frames.Count < 2 ? 0 : _frames.Skip(1).Sum(f => (double)f.Bytes) / 1_048_576 / Math.Max((_frames[^1].At - _frames[0].At).TotalSeconds, 1e-9);

    /// <summary>The longest wait between two frames — the stutter a person sees.</summary>
    public TimeSpan LongestGap =>
        _frames.Count < 2 ? TimeSpan.Zero : _frames.Zip(_frames.Skip(1), (a, b) => b.At - a.At).Max();
}

/// <summary><c>key=value</c> lines, as both of the emulator's files are written.</summary>
internal static class KeyValues
{
    public static Dictionary<string, string> Parse(string text)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=', StringComparison.Ordinal);

            if (line.Length == 0 || line[0] is '#' or ';' || equals <= 0)
            {
                continue;
            }

            keys[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return keys;
    }
}
