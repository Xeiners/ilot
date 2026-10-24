using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MyNotch.Modules.DropZone.Local;

/// <summary>Where this PC can be reached by a phone next to it: its address on the Wi-Fi (or cable) it is on.</summary>
/// <param name="IsWireless">On Wi-Fi rather than a cable: what the phone is told to join.</param>
public sealed record LocalEndpoint(IPAddress Address, bool IsWireless);

public static class LocalNetwork
{
    // Adapters that are not the network around the PC: VMs, VPNs, tunnels, capture drivers…
    private static readonly string[] Virtual =
    [
        "hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "tap-", "tun", "wireguard", "tailscale", "zerotier",
        "openvpn", "npcap", "bluetooth", "virtual", "loopback",
    ];

    /// <summary>
    /// The IPv4 address on the network in use: one with a gateway (a router) first, Wi-Fi before cable, private
    /// ranges before anything else. A PC sharing its own hotspot (no gateway) still comes up, last.
    /// </summary>
    public static LocalEndpoint? Find()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !Virtual.Any(word => nic.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
                        || nic.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Select(nic => (Nic: nic, Properties: nic.GetIPProperties()))
                .Select(entry => (entry.Nic, entry.Properties, Address: entry.Properties.UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && IsUsable(address))))
                .Where(entry => entry.Address is not null)
                .OrderBy(entry => HasGateway(entry.Properties) ? 0 : 1)
                .ThenBy(entry => IsPrivate(entry.Address!) ? 0 : 1)
                .ThenBy(entry => entry.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .Select(entry => new LocalEndpoint(entry.Address!, entry.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>Whether the PC still has this address (the Wi-Fi may have dropped, or changed).</summary>
    public static bool Has(IPAddress address)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static bool HasGateway(IPInterfaceProperties properties) =>
        properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork
            && !gateway.Address.Equals(IPAddress.Any));

    // Neither loopback nor link-local (169.254.x.x: no DHCP answer, nothing to reach).
    private static bool IsUsable(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return !IPAddress.IsLoopback(address) && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsPrivate(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
