using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Concentus;
using CommBluetooth.Core.Network;
using CommBluetooth.Core.Protocol;
using CommBluetooth.Core.Streaming;

namespace CommBluetooth.Core.Tests;

public class NetworkTests
{
    [Fact]
    public async Task Session_StreamsAudioToReceiverOverUdp()
    {
        var packets = new ConcurrentQueue<(PacketHeader Header, byte[] Payload)>();
        using var receiver = new AudioReceiver(0);
        receiver.PacketReceived += (header, payload, _) => packets.Enqueue((header, payload.ToArray()));
        receiver.Start();

        // 0,5 s de son 44,1 kHz envoyé par blocs de 10 ms, comme la capture Windows.
        var input = TestSignals.Sine(1000, 44100, 2, 0.5);
        using (var session = new StreamingSession(new IPEndPoint(IPAddress.Loopback, receiver.Port), 128_000))
        {
            for (int offset = 0; offset < input.Length; offset += 882)
                session.Push(input.AsSpan(offset, Math.Min(882, input.Length - offset)), 2, 44100);

            await WaitUntil(() => packets.Count >= 45);
        }
        await WaitUntil(() => packets.Any(p => p.Header.Type == PacketType.Stop));

        var audio = packets.Where(p => p.Header.Type == PacketType.Audio).ToList();
        Assert.InRange(audio.Count, 45, 50);
        Assert.Single(audio.Select(p => p.Header.SessionId).Distinct());
        Assert.Equal(Enumerable.Range(0, audio.Count).Select(i => (uint)i), audio.Select(p => p.Header.Sequence));

        var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        var decoded = new List<float>();
        var pcm = new float[StreamFormat.FrameInterleavedSamples];
        foreach (var (_, payload) in audio)
        {
            int samples = decoder.Decode(payload, pcm, StreamFormat.FrameSamplesPerChannel, false);
            decoded.AddRange(pcm.AsSpan(0, samples * 2).ToArray());
        }
        Assert.InRange(TestSignals.EstimateFrequency(decoded.ToArray().AsSpan(4800), 2, 0, 48000), 985, 1015);
    }

    [Fact]
    public async Task Sender_SendsKeepaliveWhenIdle()
    {
        var types = new ConcurrentQueue<PacketType>();
        using var receiver = new AudioReceiver(0);
        receiver.PacketReceived += (header, _, _) => types.Enqueue(header.Type);
        receiver.Start();

        using (new AudioSender(new IPEndPoint(IPAddress.Loopback, receiver.Port)))
            await WaitUntil(() => types.Contains(PacketType.Keepalive));

        Assert.Contains(PacketType.Keepalive, types);
    }

    [Fact]
    public async Task Discovery_ListsAnnouncedReceivers()
    {
        int port = FreeUdpPort();
        using var discovery = new ReceiverDiscovery(port);
        using var udp = new UdpClient();
        var packet = new ReceiverAnnouncement("Téléphone", 50999).ToPacket();

        await WaitUntil(() =>
        {
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
            return discovery.Receivers.Count == 1;
        });

        var receiver = Assert.Single(discovery.Receivers);
        Assert.Equal("Téléphone", receiver.Name);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 50999), receiver.AudioEndPoint);
    }

    [Fact]
    public void Announcer_AlwaysIncludesLimitedBroadcast()
    {
        Assert.Contains(IPAddress.Broadcast, ReceiverAnnouncer.BroadcastAddresses());
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition non atteinte en 5 s.");
            await Task.Delay(50);
        }
    }
}
