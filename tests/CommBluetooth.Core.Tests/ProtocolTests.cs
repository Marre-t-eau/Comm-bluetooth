using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_RoundTrips()
    {
        var buffer = new byte[PacketHeader.Size];
        var header = new PacketHeader(PacketType.Audio, 0xDEADBEEF, 123456);
        header.Write(buffer);

        Assert.True(PacketHeader.TryRead(buffer, out var read));
        Assert.Equal(header, read);
    }

    [Theory]
    [InlineData(0, (byte)'X')]   // mauvaise signature
    [InlineData(3, (byte)99)]    // version inconnue
    [InlineData(4, (byte)200)]   // type inconnu
    public void Header_RejectsInvalidPackets(int index, byte value)
    {
        var buffer = new byte[PacketHeader.Size];
        new PacketHeader(PacketType.Audio, 1, 1).Write(buffer);
        buffer[index] = value;

        Assert.False(PacketHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void Header_RejectsShortPackets()
    {
        Assert.False(PacketHeader.TryRead(new byte[PacketHeader.Size - 1], out _));
    }

    [Fact]
    public void Announcement_RoundTripsUnicodeName()
    {
        var announcement = new ReceiverAnnouncement("Téléphone de Black 🎧", 50506);

        Assert.True(ReceiverAnnouncement.TryParse(announcement.ToPacket(), out var parsed));
        Assert.Equal(announcement, parsed);
    }

    [Fact]
    public void Announcement_IgnoresOtherPacketTypes()
    {
        var buffer = new byte[PacketHeader.Size + 4];
        new PacketHeader(PacketType.Audio, 1, 1).Write(buffer);

        Assert.False(ReceiverAnnouncement.TryParse(buffer, out _));
    }
}
