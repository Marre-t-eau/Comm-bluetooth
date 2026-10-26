using Concentus;
using CommBluetooth.Core.Audio;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Tests;

public class AudioTests
{
    [Fact]
    public void Converter_PassesThroughStereo48k()
    {
        var input = TestSignals.Sine(1000, 48000, 2, 0.1);
        var output = new StereoConverter().Process(input, 2, 48000);

        Assert.Equal(input, output.ToArray());
    }

    [Fact]
    public void Converter_DuplicatesMonoOnBothChannels()
    {
        float[] input = [0.1f, -0.2f, 0.3f];
        var output = new StereoConverter().Process(input, 1, 48000).ToArray();

        Assert.Equal([0.1f, 0.1f, -0.2f, -0.2f, 0.3f, 0.3f], output);
    }

    [Fact]
    public void Converter_DownmixesSurroundWithoutClipping()
    {
        // 5.1 : FL, FR, FC, LFE, SL, SR, tous à fond.
        float[] input = [1f, 1f, 1f, 1f, 1f, 1f];
        var output = new StereoConverter().Process(input, 6, 48000).ToArray();

        Assert.Equal([1f, 1f], output);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(96000)]
    public void Converter_ResamplesTo48kAndKeepsPitch(int inputRate)
    {
        var converter = new StereoConverter();
        var collected = new List<float>();
        var input = TestSignals.Sine(1000, inputRate, 2, 1.0);

        // Par blocs de 10 ms, comme le ferait la capture Windows.
        int block = inputRate / 100 * 2;
        for (int offset = 0; offset < input.Length; offset += block)
            collected.AddRange(converter.Process(input.AsSpan(offset, Math.Min(block, input.Length - offset)), 2, inputRate).ToArray());

        int frames = collected.Count / 2;
        Assert.InRange(frames, 47000, 48100);

        var steady = collected.ToArray().AsSpan(4800, 38400); // on ignore le démarrage du filtre
        Assert.InRange(TestSignals.EstimateFrequency(steady, 2, 0, 48000), 990, 1010);
        Assert.InRange(TestSignals.EstimateFrequency(steady, 2, 1, 48000), 990, 1010);
    }

    [Fact]
    public void Encoder_ProducesOneFramePer10msAndDecodesBack()
    {
        var encoder = new OpusFrameEncoder();
        var frames = new List<byte[]>();
        var input = TestSignals.Sine(440, 48000, 2, 1.0);

        // Blocs de taille irrégulière pour vérifier le découpage.
        int offset = 0, size = 333;
        while (offset < input.Length)
        {
            int take = Math.Min(size, input.Length - offset);
            encoder.Push(input.AsSpan(offset, take), f => frames.Add(f.ToArray()));
            offset += take;
            size = size * 7 % 2000 + 1;
        }

        Assert.Equal(100, frames.Count);
        Assert.All(frames, f => Assert.InRange(f.Length, 1, StreamFormat.MaxOpusFrameBytes));

        var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        var decoded = new List<float>();
        var pcm = new float[StreamFormat.FrameInterleavedSamples];
        foreach (var frame in frames)
        {
            int samples = decoder.Decode(frame, pcm, StreamFormat.FrameSamplesPerChannel, false);
            decoded.AddRange(pcm.AsSpan(0, samples * 2).ToArray());
        }

        var steady = decoded.ToArray().AsSpan(4800);
        Assert.InRange(TestSignals.EstimateFrequency(steady, 2, 0, 48000), 435, 445);
        Assert.InRange(TestSignals.Rms(steady), 0.3, 0.42); // sinus d'amplitude 0,5 → RMS ≈ 0,354
    }

    [Fact]
    public void Encoder_ClampsBitrate()
    {
        var encoder = new OpusFrameEncoder { Bitrate = 1_000_000 };
        Assert.Equal(510_000, encoder.Bitrate);
    }
}
