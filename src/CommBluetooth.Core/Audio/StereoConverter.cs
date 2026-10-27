using CommBluetooth.Core.Protocol;
using NAudio.Dsp;

namespace CommBluetooth.Core.Audio;

/// <summary>
/// Convertit un flux float entrelacé quelconque (mono, stéréo, 5.1…, n'importe quelle fréquence)
/// vers le format du flux réseau : stéréo 48 kHz.
/// </summary>
public sealed class StereoConverter
{
    private const float CenterGain = 0.7071f;

    private WdlResampler? _resampler;
    private int _inputRate;
    private float[] _stereo = [];
    private float[] _output = [];

    /// <summary>
    /// Convertit un bloc. Le résultat pointe vers un tampon interne valable jusqu'au prochain appel.
    /// </summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

        int frames = interleaved.Length / channels;
        if (frames == 0)
            return [];

        EnsureCapacity(ref _stereo, frames * 2);
        var stereo = _stereo.AsSpan(0, frames * 2);
        Downmix(interleaved[..(frames * channels)], channels, stereo);

        if (sampleRate == StreamFormat.SampleRate)
        {
            _resampler = null;
            return stereo;
        }

        if (_resampler is null || _inputRate != sampleRate)
            CreateResampler(sampleRate);

        int needed = _resampler!.ResamplePrepare(frames, 2, out var input);
        int fed = Math.Min(needed, frames);
        stereo[..(fed * 2)].CopyTo(input);

        int maxOut = (int)Math.Ceiling(fed * (double)StreamFormat.SampleRate / sampleRate) + 16;
        EnsureCapacity(ref _output, maxOut * 2);
        int produced = _resampler.ResampleOut(_output, fed, maxOut, 2);
        return _output.AsSpan(0, produced * 2);
    }

    public void Reset()
    {
        _resampler = null;
        _inputRate = 0;
    }

    private void CreateResampler(int sampleRate)
    {
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false, 64, 32);
        _resampler.SetFilterParms(0.693f, 0.707f);
        _resampler.SetFeedMode(true);
        _resampler.SetRates(sampleRate, StreamFormat.SampleRate);
        _inputRate = sampleRate;
    }

    private static void Downmix(ReadOnlySpan<float> source, int channels, Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        switch (channels)
        {
            case 1:
                for (int i = 0; i < frames; i++)
                    stereo[2 * i] = stereo[2 * i + 1] = source[i];
                break;

            case 2:
                source.CopyTo(stereo);
                break;

            default:
                // Ordre WAVE standard : FL, FR, FC, LFE, puis les canaux arrière/côté par paires.
                for (int i = 0; i < frames; i++)
                {
                    var frame = source.Slice(i * channels, channels);
                    float left = frame[0];
                    float right = frame[1];
                    if (channels >= 3)
                    {
                        left += frame[2] * CenterGain;
                        right += frame[2] * CenterGain;
                    }
                    for (int c = 4; c + 1 < channels; c += 2)
                    {
                        left += frame[c] * CenterGain;
                        right += frame[c + 1] * CenterGain;
                    }
                    stereo[2 * i] = Math.Clamp(left, -1f, 1f);
                    stereo[2 * i + 1] = Math.Clamp(right, -1f, 1f);
                }
                break;
        }
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
            buffer = new float[Math.Max(length, buffer.Length * 2)];
    }
}
