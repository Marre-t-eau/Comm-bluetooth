using System.Net;
using System.Net.Sockets;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Network;

public delegate void PacketReceivedHandler(PacketHeader header, ReadOnlyMemory<byte> payload, IPEndPoint sender);

/// <summary>
/// Côté récepteur : écoute les paquets envoyés par le PC sur le port audio.
/// </summary>
public sealed class AudioReceiver : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();

    public AudioReceiver(int port = StreamFormat.DefaultAudioPort)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ReceiveBufferSize = 256 * 1024;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>Port réellement utilisé (utile si 0 a été demandé).</summary>
    public int Port { get; }

    /// <summary>Appelé sur le thread de réception pour chaque paquet valide.</summary>
    public event PacketReceivedHandler? PacketReceived;

    public void Start() => _ = ListenAsync(_cts.Token);

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

            if (PacketHeader.TryRead(result.Buffer, out var header))
                PacketReceived?.Invoke(header, result.Buffer.AsMemory(PacketHeader.Size), result.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
