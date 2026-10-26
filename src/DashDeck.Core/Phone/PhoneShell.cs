using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DashDeck.Core.Phone;

/// <summary>
/// The shell commands the dash runs on the phone, and reading what comes back (ADR-0076). Read-only
/// towards the truck: all of this is the phone.
/// </summary>
public static class PhoneShell
{
    /// <summary>The port ADB listens on over the network once it is told to (<c>adb tcpip</c>).</summary>
    public const int WirelessPort = 5555;

    /// <summary>The phone's screen given back its own size, density and way of turning.</summary>
    public const string PutBack = "wm size reset; wm density reset; cmd window set-ignore-orientation-request false";

    /// <summary>
    /// On Android 12 and later, an app that insists on one way round is letterboxed rather than
    /// turning the screen. Older Androids have no such setting and say so; that is not a failure.
    /// </summary>
    public const string LetterboxApps = "cmd window set-ignore-orientation-request true";

    /// <summary>
    /// A watchdog, started on the phone beside the mirror, for when the mirror ends without the dash
    /// saying so — the cable pulled, the Wi-Fi lost, the dash gone — so the phone is never left at the
    /// region's odd size. It waits while scrcpy's server runs, then puts the screen back. Detached
    /// (<c>setsid</c>, nothing held open), so it outlives the ADB connection that started it. The
    /// pattern is bracketed so it cannot match the watchdog's own command line.
    /// </summary>
    public const string Watchdog =
        "setsid sh -c 'sleep 10; while pgrep -f \"[c]om.genymobile.scrcpy.Server\" >/dev/null; do sleep 3; done; "
        + PutBack + "' </dev/null >/dev/null 2>&1 &";

    /// <summary>The phone's IPv4 addresses, one line each.</summary>
    public const string AddressesCommand = "ip -o -4 addr show";

    /// <summary>Night, as the dash has it, as the phone's dark theme.</summary>
    public static string Night(bool dark) => dark ? "cmd uimode night yes" : "cmd uimode night no";

    /// <summary>
    /// The phone's Wi-Fi addresses from <see cref="AddressesCommand"/>'s answer: its connection to a
    /// network (<c>wlan0</c>) first, then its own hotspot (<c>wlan1</c>, <c>ap0</c>, <c>swlan0</c>).
    /// Never mobile data, loopback or Wi-Fi Direct.
    /// </summary>
    public static IReadOnlyList<string> Addresses(string? output)
    {
        var found = new List<(string Interface, string Address)>();

        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var inet = Array.IndexOf(words, "inet");
            if (words.Length < 4 || inet < 2 || inet + 1 >= words.Length)
            {
                continue;
            }

            var name = words[1].TrimEnd(':');
            var address = words[inet + 1].Split('/')[0];
            var wifi = name.StartsWith("wlan", StringComparison.Ordinal) || name.StartsWith("ap", StringComparison.Ordinal)
                || name.StartsWith("swlan", StringComparison.Ordinal) || name.StartsWith("softap", StringComparison.Ordinal);

            if (wifi && IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            {
                found.Add((name, address));
            }
        }

        return [.. found.OrderBy(f => f.Interface == "wlan0" ? 0 : 1).ThenBy(f => f.Interface, StringComparer.Ordinal).Select(f => f.Address).Distinct()];
    }

    /// <summary>
    /// Where to look for the phone over the network before asking ADB for devices: the address it was
    /// last reached at, then each gateway of the tablet's — which, when the tablet is on the phone's
    /// own hotspot, as in the truck, is the phone.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? remembered, IEnumerable<string> gateways)
    {
        ArgumentNullException.ThrowIfNull(gateways);

        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            list.Add(remembered.Trim());
        }

        list.AddRange(gateways
            .Where(g => IPAddress.TryParse(g, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any))
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g}:{WirelessPort}")));

        return [.. list.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>True when <c>adb connect</c> reached the phone (it can exit cleanly having failed).</summary>
    public static bool Connected(string? output) =>
        (output ?? "").Contains("connected to", StringComparison.OrdinalIgnoreCase)
        && !(output ?? "").Contains("failed", StringComparison.OrdinalIgnoreCase)
        && !(output ?? "").Contains("cannot", StringComparison.OrdinalIgnoreCase);
}
