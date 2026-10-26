using System.Buffers.Binary;
using System.Text;

namespace CommBluetooth.Core.Protocol;

public enum PacketType : byte
{
    /// <summary>Récepteur → broadcast : « je suis là ». Charge utile : port audio (u16) + nom UTF-8.</summary>
    Announce = 1,

    /// <summary>PC → récepteur : une trame Opus.</summary>
    Audio = 2,

    /// <summary>PC → récepteur : la session est toujours active même sans son (silence).</summary>
    Keepalive = 3,

    /// <summary>PC → récepteur : fin de session.</summary>
    Stop = 4,
}

/// <summary>
/// En-tête commun à tous les paquets UDP (16 octets, gros-boutiste) :
/// <code>
/// 0  'C' 'B' 'T'  magie
/// 3  version
/// 4  type
/// 5  réservé (3 octets)
/// 8  identifiant de session (u32)
/// 12 numéro de séquence (u32)
/// </code>
/// </summary>
public readonly record struct PacketHeader(PacketType Type, uint SessionId, uint Sequence)
{
    public const int Size = 16;
    public const byte Version = 1;

    private static ReadOnlySpan<byte> Magic => "CBT"u8;

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Tampon trop petit pour l'en-tête.", nameof(destination));

        Magic.CopyTo(destination);
        destination[3] = Version;
        destination[4] = (byte)Type;
        destination[5] = destination[6] = destination[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], SessionId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], Sequence);
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, out PacketHeader header)
    {
        header = default;
        if (packet.Length < Size || !packet[..3].SequenceEqual(Magic) || packet[3] != Version)
            return false;

        var type = (PacketType)packet[4];
        if (!Enum.IsDefined(type))
            return false;

        header = new PacketHeader(
            type,
            BinaryPrimitives.ReadUInt32BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[12..]));
        return true;
    }
}

/// <summary>Annonce d'un récepteur trouvé sur le réseau.</summary>
public sealed record ReceiverAnnouncement(string Name, int AudioPort)
{
    private const int MaxNameBytes = 64;

    public byte[] ToPacket(uint sessionId = 0, uint sequence = 0)
    {
        var name = Encoding.UTF8.GetBytes(Name);
        if (name.Length > MaxNameBytes)
            name = name[..MaxNameBytes];

        var packet = new byte[PacketHeader.Size + 2 + name.Length];
        new PacketHeader(PacketType.Announce, sessionId, sequence).Write(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(PacketHeader.Size), (ushort)AudioPort);
        name.CopyTo(packet, PacketHeader.Size + 2);
        return packet;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out ReceiverAnnouncement? announcement)
    {
        announcement = null;
        if (!PacketHeader.TryRead(packet, out var header) || header.Type != PacketType.Announce)
            return false;

        var payload = packet[PacketHeader.Size..];
        if (payload.Length < 2)
            return false;

        int port = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (port == 0)
            return false;

        string name;
        try
        {
            name = new UTF8Encoding(false, true).GetString(payload[2..]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        announcement = new ReceiverAnnouncement(name, port);
        return true;
    }
}
