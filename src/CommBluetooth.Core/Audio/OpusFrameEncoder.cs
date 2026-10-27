using Concentus;
using Concentus.Enums;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Audio;

/// <summary>
/// Découpe un flux stéréo 48 kHz en trames de 10 ms et les encode en Opus.
/// </summary>
public sealed class OpusFrameEncoder
{
    private readonly IOpusEncoder _encoder;
    private readonly float[] _pending = new float[StreamFormat.FrameInterleavedSamples];
    private readonly byte[] _packet = new byte[StreamFormat.MaxOpusFrameBytes];
    private int _pendingCount;

    public OpusFrameEncoder(int bitrate = StreamFormat.DefaultBitrate)
    {
        _encoder = OpusCodecFactory.CreateEncoder(
            StreamFormat.SampleRate, StreamFormat.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        _encoder.UseVBR = true;
        _encoder.Complexity = 8;
        // Mode CELT seul (faible délai) : une trame perdue est masquée côté récepteur par le PLC d'Opus.
        Bitrate = bitrate;
    }

    public int Bitrate
    {
        get => _encoder.Bitrate;
        set => _encoder.Bitrate = Math.Clamp(value, 32_000, 510_000);
    }

    /// <summary>
    /// Ajoute des échantillons stéréo 48 kHz entrelacés ; <paramref name="onFrame"/> est appelé
    /// pour chaque trame Opus complète (le tampon passé n'est valide que pendant l'appel).
    /// </summary>
    public void Push(ReadOnlySpan<float> stereo48k, Action<ReadOnlyMemory<byte>> onFrame)
    {
        while (!stereo48k.IsEmpty)
        {
            int take = Math.Min(stereo48k.Length, _pending.Length - _pendingCount);
            stereo48k[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            stereo48k = stereo48k[take..];

            if (_pendingCount == _pending.Length)
            {
                int length = _encoder.Encode(_pending, StreamFormat.FrameSamplesPerChannel, _packet, _packet.Length);
                _pendingCount = 0;
                onFrame(_packet.AsMemory(0, length));
            }
        }
    }

    public void Reset()
    {
        _pendingCount = 0;
        _encoder.ResetState();
    }
}
