using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Network;

public sealed record DiscoveredReceiver(string Name, IPEndPoint AudioEndPoint, DateTime LastSeenUtc)
{
    public override string ToString() => $"{Name} ({AudioEndPoint.Address})";
}

/// <summary>
/// Écoute les annonces broadcast des récepteurs (téléphones) présents sur le réseau local.
/// </summary>
public sealed class ReceiverDiscovery : IDisposable
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<IPAddress, DiscoveredReceiver> _receivers = new();
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _pruneTimer;

    public ReceiverDiscovery(int port = StreamFormat.DiscoveryPort)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _ = ListenAsync(_cts.Token);
        _pruneTimer = new Timer(_ => Prune(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Déclenché (sur un thread du pool) quand la liste change.</summary>
    public event EventHandler? ReceiversChanged;

    public IReadOnlyList<DiscoveredReceiver> Receivers =>
        _receivers.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (!ReceiverAnnouncement.TryParse(result.Buffer, out var announcement))
                continue;

            var address = result.RemoteEndPoint.Address;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            var receiver = new DiscoveredReceiver(
                announcement!.Name, new IPEndPoint(address, announcement.AudioPort), DateTime.UtcNow);
            bool isNew = !_receivers.TryGetValue(address, out var previous)
                || previous.Name != receiver.Name
                || previous.AudioEndPoint.Port != receiver.AudioEndPoint.Port;
            _receivers[address] = receiver;
            if (isNew)
                ReceiversChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Prune()
    {
        bool changed = false;
        var limit = DateTime.UtcNow - Expiry;
        foreach (var (address, receiver) in _receivers)
        {
            if (receiver.LastSeenUtc < limit && _receivers.TryRemove(address, out _))
                changed = true;
        }
        if (changed)
            ReceiversChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pruneTimer.Dispose();
        _udp.Dispose();
        _cts.Dispose();
    }
}
