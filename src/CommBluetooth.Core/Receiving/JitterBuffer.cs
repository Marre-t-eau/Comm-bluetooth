using System.Diagnostics;
using Concentus;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Core.Receiving;

/// <summary>
/// Tampon anti-gigue côté récepteur. Le réseau livre les trames Opus de façon irrégulière
/// (en retard, dans le désordre, parfois jamais) ; la lecture audio, elle, en réclame une
/// toutes les 10 ms. Ce tampon garde quelques trames d'avance, les remet dans l'ordre,
/// masque les trames perdues (PLC d'Opus) et rattrape la dérive entre l'horloge du PC et
/// celle du téléphone.
/// <para>
/// La réserve s'adapte : elle grossit après une coupure due au réseau, puis redescend vers
/// <see cref="TargetFrames"/> quand le Wi-Fi est calme, et l'avance qui s'installe durablement
/// au-dessus de la cible est résorbée petit à petit pour ne pas laisser la latence dériver.
/// </para>
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>Au-delà de cet écart de séquence, on considère que le flux a sauté et on resynchronise.</summary>
    private const int MaxSequenceJump = 200;

    /// <summary>Fenêtre d'observation de la réserve minimale (1 s de lecture).</summary>
    private const int TrimWindowFrames = 100;

    /// <summary>Marge ajoutée après chaque coupure due au réseau, et plafond de cette marge.</summary>
    private const int ExtraFramesPerGlitch = 2;
    private const int MaxExtraFrames = 10;

    /// <summary>Sans nouvelle coupure pendant ce nombre de trames (20 s), on retire une trame de marge.</summary>
    private const int CalmFramesBeforeShrink = 2000;

    /// <summary>
    /// Une reprise du flux moins de 250 ms après la panne sèche indique un trou réseau.
    /// Au-delà, le PC s'était simplement tu (rien ne jouait) et il n'y a rien à corriger.
    /// </summary>
    private static readonly TimeSpan GlitchWindow = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<uint, byte[]> _frames = [];
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(StreamFormat.SampleRate, StreamFormat.Channels);
    private readonly Lock _gate = new();
    private readonly Func<TimeSpan> _clock;
    private uint? _sessionId;
    private uint _nextSequence;
    private bool _buffering = true;
    private bool _started;
    private int _targetFrames;
    private int _extraFrames;
    private int _calmFrames;
    private int _windowReads;
    private int _windowMin = int.MaxValue;
    private TimeSpan? _underrunAt;
    private TimeSpan? _lastArrival;
    private TimeSpan _gapWindowStart;
    private TimeSpan _gapWindowMax;
    private TimeSpan _lastGapWindowMax;

    public JitterBuffer(int targetFrames = 6, Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
        TargetFrames = targetFrames;
    }

    /// <summary>Réserve minimale voulue, en trames de 10 ms : c'est la latence ajoutée par le tampon.</summary>
    public int TargetFrames
    {
        get => _targetFrames;
        set
        {
            lock (_gate)
            {
                _targetFrames = Math.Clamp(value, 1, 50);
                _extraFrames = 0;
            }
        }
    }

    /// <summary>Réserve réellement visée : la cible plus la marge ajoutée après des coupures.</summary>
    public int EffectiveTargetFrames { get { lock (_gate) return _targetFrames + _extraFrames; } }

    public int BufferedFrames { get { lock (_gate) return _frames.Count; } }

    /// <summary>
    /// Plus grand écart entre deux paquets audio reçus sur les 2 dernières secondes, en ms.
    /// Le PC en envoie un toutes les 10 ms : un écart de 100 ms révèle un Wi-Fi qui livre par rafales
    /// (économie d'énergie), et c'est ce qui oblige le tampon à grossir.
    /// </summary>
    public int MaxArrivalGapMs
    {
        get { lock (_gate) return (int)Math.Max(_gapWindowMax.TotalMilliseconds, _lastGapWindowMax.TotalMilliseconds); }
    }
    public long FramesPlayed { get; private set; }
    public long FramesConcealed { get; private set; }
    public long FramesDropped { get; private set; }
    public long FramesTrimmed { get; private set; }
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

            var now = _clock();
            TrackArrivalGap(now);

            if (_underrunAt is { } underrunAt)
            {
                if (now - underrunAt < GlitchWindow)
                {
                    // Le flux reprend juste après la panne : c'était le réseau, on prend plus de marge.
                    _extraFrames = Math.Min(_extraFrames + ExtraFramesPerGlitch, MaxExtraFrames);
                    _calmFrames = 0;
                }
                _underrunAt = null;
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
            int target = _targetFrames + _extraFrames;
            if (_buffering)
            {
                if (_frames.Count < target)
                {
                    output.Clear();
                    return false;
                }
                _buffering = false;
                ResetTrimWindow();
            }

            if (_frames.Count == 0)
            {
                // Plus rien en réserve : le PC s'est tu ou le réseau a décroché. On se remet à accumuler.
                Underruns++;
                _buffering = true;
                _underrunAt = _clock();
                output.Clear();
                return false;
            }

            // Rafale réseau : beaucoup trop d'avance d'un coup, on saute immédiatement les plus anciennes.
            int excess = _frames.Count - (target + Math.Max(10, target));
            while (excess > 0)
            {
                if (DropNextLocked())
                    excess--;
            }

            // Dérive lente : si la réserve n'est jamais descendue jusqu'à la cible pendant toute
            // une seconde, l'excédent est inutile et ne fait qu'ajouter du décalage. On retire une
            // trame par seconde (10 ms), assez discret pour ne pas s'entendre.
            _windowMin = Math.Min(_windowMin, _frames.Count);
            if (++_windowReads >= TrimWindowFrames)
            {
                if (_windowMin > target && DropNextLocked())
                    FramesTrimmed++;
                ResetTrimWindow();
            }

            if (_extraFrames > 0 && ++_calmFrames >= CalmFramesBeforeShrink)
            {
                _extraFrames--;
                _calmFrames = 0;
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

    private void TrackArrivalGap(TimeSpan now)
    {
        // Les pauses du PC (rien ne joue) ne sont pas de la gigue réseau : on ignore les écarts d'une seconde ou plus.
        if (_lastArrival is { } last && now - last < TimeSpan.FromSeconds(1) && now - last > _gapWindowMax)
            _gapWindowMax = now - last;
        _lastArrival = now;

        if (now - _gapWindowStart >= TimeSpan.FromSeconds(2))
        {
            _lastGapWindowMax = _gapWindowMax;
            _gapWindowMax = TimeSpan.Zero;
            _gapWindowStart = now;
        }
    }

    /// <summary>Saute la prochaine position de lecture ; renvoie true si une trame reçue a été jetée.</summary>
    private bool DropNextLocked()
    {
        bool dropped = _frames.Remove(_nextSequence);
        if (dropped)
            FramesDropped++;
        _nextSequence++;
        return dropped;
    }

    private void ResetTrimWindow()
    {
        _windowReads = 0;
        _windowMin = int.MaxValue;
    }

    private void ResetLocked(uint? sessionId, uint nextSequence)
    {
        _frames.Clear();
        _sessionId = sessionId;
        _nextSequence = nextSequence;
        _buffering = true;
        _started = false;
        _underrunAt = null;
        ResetTrimWindow();
        _decoder.ResetState();
    }
}
