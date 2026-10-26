namespace CommBluetooth.Core.Tests;

internal static class TestSignals
{
    /// <summary>Sinusoïde entrelacée (même signal sur tous les canaux).</summary>
    public static float[] Sine(double frequency, int sampleRate, int channels, double seconds, float amplitude = 0.5f)
    {
        int frames = (int)(sampleRate * seconds);
        var data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float value = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            for (int c = 0; c < channels; c++)
                data[i * channels + c] = value;
        }
        return data;
    }

    /// <summary>Estime la fréquence dominante d'un canal par comptage des passages par zéro.</summary>
    public static double EstimateFrequency(ReadOnlySpan<float> interleaved, int channels, int channel, int sampleRate)
    {
        int frames = interleaved.Length / channels;
        int crossings = 0;
        for (int i = 1; i < frames; i++)
        {
            float previous = interleaved[(i - 1) * channels + channel];
            float current = interleaved[i * channels + channel];
            if (previous < 0 && current >= 0)
                crossings++;
        }
        return crossings * (double)sampleRate / frames;
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }
}
