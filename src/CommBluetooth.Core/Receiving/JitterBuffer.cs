using Concentus;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Receiving;

/// <summary>
/// Tampon anti-gigue côté récepteur. Le réseau livre les trames Opus de façon irrégulière
/// (en retard, dans le désordre, parfois jamais) ; la lecture audio, elle, en réclame une
/// toutes les 10 ms. Ce tampon garde quelques trames d'avance, les remet dans l'ordre,
/// masque les trames perdues (PLC d'Opus) et rattrape la dérive entre l'horloge du PC et
/// celle du téléphone.
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>Au-delà de cet écart de séquence, on considère que le flux a sauté et on resynchronise.</summary>
    private const int MaxSequenceJump = 200;

    private readonly Dictionary<uint, byte[]> _frames = [];
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(StreamFormat.SampleRate, StreamFormat.Channels);
    private readonly Lock _gate = new();
    private uint? _sessionId;
    private uint _nextSequence;
    private bool _buffering = true;
    private bool _started;
    private int _targetFrames;

    public JitterBuffer(int targetFrames = 6)
    {
        TargetFrames = targetFrames;
    }

    /// <summary>Nombre de trames (de 10 ms) à accumuler avant de jouer : c'est la latence ajoutée.</summary>
    public int TargetFrames
    {
        get => _targetFrames;
        set => _targetFrames = Math.Clamp(value, 1, 50);
    }

    public int BufferedFrames { get { lock (_gate) return _frames.Count; } }
    public long FramesPlayed { get; private set; }
    public long FramesConcealed { get; private set; }
    public long FramesDropped { get; private set; }
    public long Underruns { get; private set; }

    /// <summary>Ajoute un paquet reçu du réseau (appelé par le thread de réception).</summary>
    public void Push(PacketHeader header, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (header.Type == PacketType.Stop)
            {
                // On laisse finir ce qui est déjà en réserve ; la prochaine session repartira de zéro.
                if (header.SessionId == _sessionId)
                    _sessionId = null;
                return;
            }

            if (header.SessionId != _sessionId)
                ResetLocked(header.SessionId, header.Sequence);

            if (header.Type != PacketType.Audio)
                return;

            int distance = (int)(header.Sequence - _nextSequence);
            if (distance < 0 && !_started)
            {
                // Début de session dans le désordre : rien n'a encore été joué, on recule simplement.
                _nextSequence = header.Sequence;
            }
            else if (distance < 0)
            {
                // Arrivée trop tard : sa place a déjà été jouée (ou masquée).
                FramesDropped++;
                return;
            }
            if (distance > MaxSequenceJump)
            {
                ResetLocked(header.SessionId, header.Sequence);
            }

            _frames[header.Sequence] = payload.ToArray();
        }
    }

    /// <summary>
    /// Fournit la prochaine trame de 10 ms (960 floats stéréo entrelacés) pour la lecture.
    /// Renvoie false et remplit de silence quand il n'y a rien à jouer.
    /// </summary>
    public bool ReadFrame(Span<float> output)
    {
        if (output.Length < StreamFormat.FrameInterleavedSamples)
            throw new ArgumentException("Le tampon doit contenir une trame complète.", nameof(output));
        output = output[..StreamFormat.FrameInterleavedSamples];

        lock (_gate)
        {
            if (_buffering)
            {
                if (_frames.Count < _targetFrames)
                {
                    output.Clear();
                    return false;
                }
                _buffering = false;
            }

            if (_frames.Count == 0)
            {
                // Plus rien en réserve : le PC s'est tu ou le réseau a décroché. On se remet à accumuler.
                Underruns++;
                _buffering = true;
                output.Clear();
                return false;
            }

            // Dérive d'horloge ou rafale réseau : trop d'avance accumulée, on saute les plus anciennes.
            int excess = _frames.Count - (_targetFrames + Math.Max(10, _targetFrames));
            while (excess > 0)
            {
                if (_frames.Remove(_nextSequence))
                {
                    FramesDropped++;
                    excess--;
                }
                _nextSequence++;
            }

            int samples;
            if (_frames.Remove(_nextSequence, out var frame))
            {
                samples = _decoder.Decode(frame, output, StreamFormat.FrameSamplesPerChannel, false);
            }
            else
            {
                samples = _decoder.Decode([], output, StreamFormat.FrameSamplesPerChannel, false);
                FramesConcealed++;
            }
            _nextSequence++;
            _started = true;
            FramesPlayed++;

            if (samples < StreamFormat.FrameSamplesPerChannel)
                output[(samples * StreamFormat.Channels)..].Clear();
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
            ResetLocked(null, 0);
    }

    private void ResetLocked(uint? sessionId, uint nextSequence)
    {
        _frames.Clear();
        _sessionId = sessionId;
        _nextSequence = nextSequence;
        _buffering = true;
        _started = false;
        _decoder.ResetState();
    }
}
