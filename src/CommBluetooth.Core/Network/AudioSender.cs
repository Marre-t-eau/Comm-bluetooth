using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Network;

/// <summary>
/// Envoie les trames Opus en UDP vers un récepteur, avec un keepalive pendant les silences
/// (WASAPI ne fournit aucune donnée quand rien ne joue sur le PC).
/// </summary>
public sealed class AudioSender : IDisposable
{
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromMilliseconds(500);

    private readonly UdpClient _udp;
    private readonly byte[] _buffer = new byte[PacketHeader.Size + StreamFormat.MaxOpusFrameBytes];
    private readonly Timer _keepalive;
    private readonly Lock _gate = new();
    private uint _sequence;
    private long _lastSendTimestamp;
    private bool _disposed;

    public AudioSender(IPEndPoint target)
    {
        Target = target;
        SessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _udp = new UdpClient(target.AddressFamily);
        _udp.Connect(target);
        _keepalive = new Timer(_ => SendKeepaliveIfIdle(), null, KeepaliveInterval, KeepaliveInterval);
    }

    public IPEndPoint Target { get; }
    public uint SessionId { get; }
    public long PacketsSent { get; private set; }
    public long BytesSent { get; private set; }

    /// <summary>Dernière erreur réseau (par exemple « hôte injoignable »), ou null.</summary>
    public string? LastError { get; private set; }

    public void SendAudio(ReadOnlySpan<byte> opusFrame) => Send(PacketType.Audio, opusFrame);

    private void SendKeepaliveIfIdle()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSendTimestamp)) >= KeepaliveInterval)
            Send(PacketType.Keepalive, []);
    }

    private void Send(PacketType type, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            // Seules les trames audio avancent la séquence : un trou côté récepteur signifie
            // donc toujours une trame perdue, à masquer.
            uint sequence = type == PacketType.Audio ? _sequence++ : _sequence;
            new PacketHeader(type, SessionId, sequence).Write(_buffer);
            payload.CopyTo(_buffer.AsSpan(PacketHeader.Size));
            int length = PacketHeader.Size + payload.Length;

            try
            {
                _udp.Send(_buffer, length);
                PacketsSent++;
                BytesSent += length;
                LastError = null;
            }
            catch (SocketException ex)
            {
                // UDP : une erreur ponctuelle (ICMP port injoignable…) ne doit pas arrêter la capture.
                LastError = ex.Message;
            }
            Interlocked.Exchange(ref _lastSendTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    public void Dispose()
    {
        _keepalive.Dispose();
        Send(PacketType.Stop, []);
        lock (_gate)
        {
            _disposed = true;
            _udp.Dispose();
        }
    }
}
