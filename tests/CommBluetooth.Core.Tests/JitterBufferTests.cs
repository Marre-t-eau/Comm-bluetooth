using System.Net;
using CommBluetooth.Core.Audio;
using CommBluetooth.Core.Protocol;
using CommBluetooth.Core.Receiving;
using CommBluetooth.Core.Streaming;

namespace CommBluetooth.Core.Tests;

public class JitterBufferTests
{
    private const uint Session = 42;

    private static List<byte[]> EncodeSine(double seconds, double frequency = 440)
    {
        var frames = new List<byte[]>();
        new OpusFrameEncoder().Push(TestSignals.Sine(frequency, 48000, 2, seconds), f => frames.Add(f.ToArray()));
        return frames;
    }

    private static void PushAudio(JitterBuffer buffer, uint sequence, byte[] frame, uint session = Session) =>
        buffer.Push(new PacketHeader(PacketType.Audio, session, sequence), frame);

    private static float[] Drain(JitterBuffer buffer, int frames, out int played)
    {
        var output = new float[frames * StreamFormat.FrameInterleavedSamples];
        played = 0;
        for (int i = 0; i < frames; i++)
        {
            if (buffer.ReadFrame(output.AsSpan(i * StreamFormat.FrameInterleavedSamples, StreamFormat.FrameInterleavedSamples)))
                played++;
        }
        return output;
    }

    [Fact]
    public void WaitsForTargetDepthBeforePlaying()
    {
        var frames = EncodeSine(0.1);
        var buffer = new JitterBuffer(targetFrames: 4);
        var output = new float[StreamFormat.FrameInterleavedSamples];

        for (uint i = 0; i < 3; i++)
            PushAudio(buffer, i, frames[(int)i]);
        Assert.False(buffer.ReadFrame(output));
        Assert.All(output, s => Assert.Equal(0f, s));

        PushAudio(buffer, 3, frames[3]);
        Assert.True(buffer.ReadFrame(output));
        Assert.Equal(3, buffer.BufferedFrames);
    }

    [Fact]
    public void ReordersOutOfOrderPackets()
    {
        var frames = EncodeSine(0.5);
        // Cible haute : tout est poussé d'avance sans déclencher le rattrapage de latence.
        var buffer = new JitterBuffer(targetFrames: 50);

        // Paquets échangés deux à deux : 1, 0, 3, 2, 5, 4…
        for (uint i = 0; i < frames.Count; i += 2)
        {
            PushAudio(buffer, i + 1, frames[(int)i + 1]);
            PushAudio(buffer, i, frames[(int)i]);
        }

        var output = Drain(buffer, frames.Count, out int played);
        Assert.Equal(frames.Count, played);
        Assert.Equal(0, buffer.FramesConcealed);
        Assert.InRange(TestSignals.EstimateFrequency(output.AsSpan(4800), 2, 0, 48000), 435, 445);
    }

    [Fact]
    public void ConcealsLostPackets()
    {
        var frames = EncodeSine(0.5);
        var buffer = new JitterBuffer(targetFrames: 40);

        for (uint i = 0; i < frames.Count; i++)
        {
            if (i % 10 != 5) // une trame sur dix perdue
                PushAudio(buffer, i, frames[(int)i]);
        }

        var output = Drain(buffer, frames.Count - 1, out _);
        Assert.Equal(5, buffer.FramesConcealed);
        // Le masquage garde un signal continu : pas de trou de silence.
        Assert.InRange(TestSignals.Rms(output.AsSpan(4800)), 0.25, 0.42);
    }

    [Fact]
    public void DropsLatePacketsAlreadyPlayed()
    {
        var frames = EncodeSine(0.1);
        var buffer = new JitterBuffer(targetFrames: 1);

        PushAudio(buffer, 0, frames[0]);
        PushAudio(buffer, 2, frames[2]);
        Drain(buffer, 2, out _); // joue 0 puis masque 1
        PushAudio(buffer, 1, frames[1]);

        Assert.Equal(1, buffer.FramesConcealed);
        Assert.Equal(1, buffer.FramesDropped);
        Assert.Equal(1, buffer.BufferedFrames);
    }

    [Fact]
    public void RebuffersAfterUnderrun()
    {
        var frames = EncodeSine(0.1);
        var buffer = new JitterBuffer(targetFrames: 2);
        var output = new float[StreamFormat.FrameInterleavedSamples];

        PushAudio(buffer, 0, frames[0]);
        PushAudio(buffer, 1, frames[1]);
        Assert.True(buffer.ReadFrame(output));
        Assert.True(buffer.ReadFrame(output));
        Assert.False(buffer.ReadFrame(output)); // vide
        Assert.Equal(1, buffer.Underruns);

        PushAudio(buffer, 2, frames[2]);
        Assert.False(buffer.ReadFrame(output)); // attend de nouveau 2 trames
        PushAudio(buffer, 3, frames[3]);
        Assert.True(buffer.ReadFrame(output));
    }

    [Fact]
    public void CatchesUpWhenTooFarAhead()
    {
        var frames = EncodeSine(0.5);
        var buffer = new JitterBuffer(targetFrames: 3);
        for (uint i = 0; i < frames.Count; i++)
            PushAudio(buffer, i, frames[(int)i]);

        var output = new float[StreamFormat.FrameInterleavedSamples];
        Assert.True(buffer.ReadFrame(output));

        // Cible 3 + marge 10 : le surplus a été jeté pour limiter la latence.
        Assert.Equal(12, buffer.BufferedFrames);
        Assert.Equal(frames.Count - 13, buffer.FramesDropped);
    }

    [Fact]
    public void NewSessionAndStopResetTheBuffer()
    {
        var frames = EncodeSine(0.1);
        var buffer = new JitterBuffer(targetFrames: 2);

        PushAudio(buffer, 100, frames[0]);
        PushAudio(buffer, 101, frames[1]);
        PushAudio(buffer, 0, frames[2], session: 7); // le PC a redémarré la diffusion
        Assert.Equal(1, buffer.BufferedFrames);

        // Fin de session : la réserve est jouée jusqu'au bout, puis une nouvelle session repart de zéro.
        buffer.Push(new PacketHeader(PacketType.Stop, 7, 1), []);
        Assert.Equal(1, buffer.BufferedFrames);
        PushAudio(buffer, 0, frames[3], session: 8);
        Assert.Equal(1, buffer.BufferedFrames);
    }

    [Fact]
    public async Task StreamReceiver_PlaysWhatTheSessionSends()
    {
        using var receiver = new StreamReceiver("Test", targetFrames: 20, port: 0, discoveryPort: FreePort());
        var input = TestSignals.Sine(1000, 48000, 2, 0.3);

        using (var session = new StreamingSession(new IPEndPoint(IPAddress.Loopback, receiver.Port), 128_000))
        {
            session.Push(input, 2, 48000);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (receiver.Buffer.BufferedFrames < 30 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(IPAddress.Loopback, receiver.Sender);
        }

        var frame = new float[StreamFormat.FrameInterleavedSamples];
        var played = new List<float>();
        while (receiver.ReadFrame(frame))
            played.AddRange(frame);

        Assert.Equal(30 * StreamFormat.FrameInterleavedSamples, played.Count);
        Assert.InRange(TestSignals.EstimateFrequency(played.ToArray().AsSpan(4800), 2, 0, 48000), 985, 1015);
        // Paquet Stop envoyé à la fin de la session.
        var stopDeadline = DateTime.UtcNow.AddSeconds(5);
        while (receiver.Sender is not null && DateTime.UtcNow < stopDeadline)
            await Task.Delay(20);
        Assert.Null(receiver.Sender);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
