using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CommBluetooth.Windows;

/// <summary>Reçoit un bloc de son capturé : float entrelacé, nombre de canaux, fréquence.</summary>
internal delegate void AudioBlockHandler(ReadOnlySpan<float> interleaved, int channels, int sampleRate);

/// <summary>
/// Capture « ce que le PC est en train de jouer » sur une sortie audio (WASAPI loopback)
/// et le convertit en float pour la suite de la chaîne.
/// </summary>
internal sealed class LoopbackAudioSource : IDisposable
{
    private readonly WasapiRecorder _capture;
    private readonly AudioBlockHandler _onAudio;
    private float[] _floats = [];

    public LoopbackAudioSource(MMDevice device, AudioBlockHandler onAudio)
    {
        _onAudio = onAudio;
        _capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .WithSharedMode()
            .Build();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
        Format = _capture.WaveFormat;
    }

    /// <summary>Déclenché quand la capture s'arrête ; l'exception est non nulle en cas d'erreur.</summary>
    public event EventHandler<Exception?>? Stopped;

    public WaveFormat Format { get; }

    public void Start() => _capture.StartRecording();

    private void OnDataAvailable(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (bytes.IsEmpty)
            return;

        // WASAPI peut marquer un paquet « silencieux » sans garantir que le tampon contient des zéros.
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            int count = bytes.Length / Format.BlockAlign * Format.Channels;
            if (_floats.Length < count)
                _floats = new float[count];
            _floats.AsSpan(0, count).Clear();
            _onAudio(_floats.AsSpan(0, count), Format.Channels, Format.SampleRate);
            return;
        }

        _onAudio(ToFloat(bytes), Format.Channels, Format.SampleRate);
    }

    private ReadOnlySpan<float> ToFloat(ReadOnlySpan<byte> bytes)
    {
        int bits = Format.BitsPerSample;
        if (IsFloat(Format) && bits == 32)
            return MemoryMarshal.Cast<byte, float>(bytes);

        int count = bytes.Length / (bits / 8);
        if (_floats.Length < count)
            _floats = new float[count];
        var output = _floats.AsSpan(0, count);

        switch (bits)
        {
            case 16:
                var shorts = MemoryMarshal.Cast<byte, short>(bytes);
                for (int i = 0; i < count; i++)
                    output[i] = shorts[i] / 32768f;
                break;
            case 24:
                for (int i = 0; i < count; i++)
                {
                    int sample = (bytes[3 * i] | bytes[3 * i + 1] << 8 | (sbyte)bytes[3 * i + 2] << 16);
                    output[i] = sample / 8388608f;
                }
                break;
            case 32:
                var ints = MemoryMarshal.Cast<byte, int>(bytes);
                for (int i = 0; i < count; i++)
                    output[i] = ints[i] / 2147483648f;
                break;
            default:
                throw new NotSupportedException($"Format audio non pris en charge : {Format}");
        }
        return output;
    }

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.StopRecording();
        _capture.Dispose();
    }
}
