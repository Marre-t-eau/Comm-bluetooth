using System.Net;
using CommBluetooth.Core.Audio;
using CommBluetooth.Core.Network;

namespace CommBluetooth.Core.Streaming;

/// <summary>
/// Chaîne complète côté PC : son capturé → stéréo 48 kHz → Opus → UDP.
/// Indépendante de Windows : la capture appelle simplement <see cref="Push"/>.
/// </summary>
public sealed class StreamingSession : IDisposable
{
    private readonly StereoConverter _converter = new();
    private readonly OpusFrameEncoder _encoder;
    private readonly AudioSender _sender;
    private readonly Action<ReadOnlyMemory<byte>> _sendFrame;
    private readonly Lock _gate = new();

    public StreamingSession(IPEndPoint target, int bitrate)
    {
        _encoder = new OpusFrameEncoder(bitrate);
        _sender = new AudioSender(target);
        _sendFrame = frame => _sender.SendAudio(frame.Span);
    }

    public IPEndPoint Target => _sender.Target;
    public long PacketsSent => _sender.PacketsSent;
    public long BytesSent => _sender.BytesSent;
    public string? LastError => _sender.LastError;

    public int Bitrate
    {
        get { lock (_gate) return _encoder.Bitrate; }
        set { lock (_gate) _encoder.Bitrate = value; }
    }

    /// <summary>Ajoute un bloc de son capturé (float entrelacé, format d'origine).</summary>
    public void Push(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        lock (_gate)
        {
            var stereo = _converter.Process(interleaved, channels, sampleRate);
            _encoder.Push(stereo, _sendFrame);
        }
    }

    public void Dispose() => _sender.Dispose();
}
