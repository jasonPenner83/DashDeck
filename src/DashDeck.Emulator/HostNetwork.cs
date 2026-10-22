using System.Net.NetworkInformation;
using System.Net.Sockets;
using DashDeck.Core.Emulation;

namespace DashDeck.Emulator;

/// <summary>The tablet's own network, as Android is to be given it (ADR-0075 addendum).</summary>
public static class HostNetwork
{
    /// <summary>
    /// DNS servers for <c>-dns-server</c> (<see cref="EmulatorNetwork.DnsServers"/>): the tablet's
    /// own from the adapters that have a gateway — the ones that reach the internet, which the
    /// hypervisor's virtual switch does not — then public ones.
    /// </summary>
    public static IReadOnlyList<string> DnsServers()
    {
        var own = new List<string>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)))
                {
                    continue;
                }

                own.AddRange(properties.DnsAddresses.Select(a => a.ToString()));
            }
        }
        catch (NetworkInformationException)
        {
            // Then the public ones alone.
        }

        return EmulatorNetwork.DnsServers(own);
    }
}
