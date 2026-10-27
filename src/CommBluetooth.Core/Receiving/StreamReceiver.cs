using System.Net;
using CommBluetooth.Core.Network;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Receiving;

/// <summary>
/// Tout le côté réception, indépendant d'Android : écoute du port audio, annonce sur le réseau
/// et tampon anti-gigue. La plateforme n'a plus qu'à appeler <see cref="ReadFrame"/> toutes les 10 ms.
/// </summary>
public sealed class StreamReceiver : IDisposable
{
    /// <summary>Sans paquet (ni audio ni keepalive) pendant ce délai, on considère le PC parti.</summary>
    private static readonly TimeSpan SenderTimeout = TimeSpan.FromSeconds(3);

    private readonly AudioReceiver _receiver;
    private readonly ReceiverAnnouncer _announcer;
    private readonly JitterBuffer _buffer;
    private long _lastPacketTimestamp;
    private IPAddress? _sender;
    private bool _stopped = true;

    public StreamReceiver(string name, int targetFrames, int port = StreamFormat.DefaultAudioPort, int discoveryPort = StreamFormat.DiscoveryPort)
    {
        _buffer = new JitterBuffer(targetFrames);
        _receiver = new AudioReceiver(port);
        _receiver.PacketReceived += OnPacket;
        _receiver.Start();
        _announcer = new ReceiverAnnouncer(name, _receiver.Port, discoveryPort);
    }

    public int Port => _receiver.Port;
    public JitterBuffer Buffer => _buffer;

    /// <summary>Adresse du PC qui envoie du son, ou null si personne n'est connecté.</summary>
    public IPAddress? Sender
    {
        get
        {
            if (_stopped || _sender is null)
                return null;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacketTimestamp));
            return elapsed < SenderTimeout ? _sender : null;
        }
    }

    private void OnPacket(PacketHeader header, ReadOnlyMemory<byte> payload, IPEndPoint from)
    {
        _buffer.Push(header, payload.Span);
        Interlocked.Exchange(ref _lastPacketTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        _sender = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
        _stopped = header.Type == PacketType.Stop;
    }

    /// <inheritdoc cref="JitterBuffer.ReadFrame"/>
    public bool ReadFrame(Span<float> output) => _buffer.ReadFrame(output);

    public void Dispose()
    {
        _announcer.Dispose();
        _receiver.Dispose();
    }
}
