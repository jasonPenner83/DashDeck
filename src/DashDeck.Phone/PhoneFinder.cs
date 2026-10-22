using System.Net.NetworkInformation;
using System.Net.Sockets;
using DashDeck.Core.Phone;

namespace DashDeck.Phone;

/// <summary>
/// Finding the phone, by cable or over the network, and moving it from one to the other (ADR-0076).
/// </summary>
/// <remarks>
/// <b>Wireless is ADB over the network</b> (<c>adb tcpip</c>), switched on once by cable. It works
/// over the house's Wi-Fi and over the phone's own hotspot — the truck's case, where the tablet joins
/// the phone's hotspot and the phone is the tablet's gateway. Android 11's Wireless debugging was not
/// used: it needs the phone on a Wi-Fi network as a client, so it is off while the phone is the
/// hotspot. The price is that the phone forgets it after a restart, and one cable connection switches
/// it back on. A computer that has not been allowed over USB is still refused: ADB's own key check
/// applies over the network too.
/// </remarks>
public static class PhoneFinder
{
    /// <summary>
    /// The phone, after trying the network first — the address it was reached at last, then each of
    /// the tablet's gateways — or null when ADB sees none. Also the network address it answered at,
    /// when it is the wireless one.
    /// </summary>
    public static async Task<(AdbDevice? Phone, string? ReachedAt)> FindAsync(string adb, string? remembered, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adb);

        var candidates = PhoneShell.Candidates(remembered, Gateways());
        var tries = candidates.Select(async address =>
        {
            var (_, output) = await Adb.RunAsync(adb, ["connect", address], TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            return PhoneShell.Connected(output) ? address : null;
        });

        var reached = (await Task.WhenAll(tries).ConfigureAwait(false)).FirstOrDefault(a => a is not null);

        var (_, devices) = await Adb.RunAsync(adb, ["devices", "-l"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var phone = AdbDevice.Pick(AdbDevice.Parse(devices));

        return (phone, phone is { IsWireless: true } ? phone.Serial : reached);
    }

    /// <summary>
    /// Switch a phone plugged in by USB to ADB over the network, and reach it there. The address it
    /// answered at, to remember — or null and why not.
    /// </summary>
    public static async Task<(string? Address, string? Problem)> GoWirelessAsync(string adb, AdbDevice phone, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adb);
        ArgumentNullException.ThrowIfNull(phone);

        var (_, ips) = await Adb.RunAsync(adb, ["-s", phone.Serial, "shell", PhoneShell.AddressesCommand], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var addresses = PhoneShell.Addresses(ips);
        if (addresses.Count == 0)
        {
            return (null, "the phone is on neither Wi-Fi nor its own hotspot");
        }

        var (switched, said) = await Adb.RunAsync(adb, ["-s", phone.Serial, "tcpip", PhoneShell.WirelessPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!switched)
        {
            return (null, $"the phone would not listen over the network: {AdbAnswers.State(said)}");
        }

        // ADB on the phone restarts to listen; give it a moment, then try each address a few times.
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            foreach (var ip in addresses)
            {
                var address = $"{ip}:{PhoneShell.WirelessPort}";
                var (_, output) = await Adb.RunAsync(adb, ["connect", address], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (PhoneShell.Connected(output))
                {
                    return (address, null);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return (null, $"the phone did not answer at {string.Join(" or ", addresses)} — is the tablet on the same Wi-Fi, or on the phone's hotspot?");
    }

    /// <summary>The tablet's IPv4 gateways, from the adapters that are up.</summary>
    private static IEnumerable<string> Gateways()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString()),
            ];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
