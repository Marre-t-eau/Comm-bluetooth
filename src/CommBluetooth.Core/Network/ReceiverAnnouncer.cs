using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Network;

/// <summary>
/// Côté récepteur : annonce sa présence chaque seconde en broadcast pour que le PC le trouve.
/// </summary>
public sealed class ReceiverAnnouncer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly byte[] _packet;
    private readonly int _discoveryPort;
    private readonly Timer _timer;

    public ReceiverAnnouncer(string name, int audioPort, int discoveryPort = StreamFormat.DiscoveryPort)
    {
        _packet = new ReceiverAnnouncement(name, audioPort).ToPacket();
        _discoveryPort = discoveryPort;
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _timer = new Timer(_ => Announce(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Announce()
    {
        foreach (var address in BroadcastAddresses())
        {
            try
            {
                _udp.Send(_packet, _packet.Length, new IPEndPoint(address, _discoveryPort));
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>
    /// 255.255.255.255 ne sort que par une seule interface : on ajoute l'adresse de broadcast
    /// de chaque réseau IPv4 actif (utile sur un PC avec plusieurs cartes réseau).
    /// </summary>
    internal static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return addresses;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    continue;

                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0))
                    continue;
                for (int i = 0; i < 4; i++)
                    ip[i] = (byte)(ip[i] | ~mask[i]);
                addresses.Add(new IPAddress(ip));
            }
        }
        return addresses;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _udp.Dispose();
    }
}
