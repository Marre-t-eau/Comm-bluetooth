namespace CommBluetooth.Core.Protocol;

/// <summary>
/// Format audio et ports réseau partagés par l'émetteur (PC) et le récepteur (téléphone).
/// </summary>
public static class StreamFormat
{
    /// <summary>Port UDP sur lequel les récepteurs annoncent leur présence (broadcast).</summary>
    public const int DiscoveryPort = 50505;

    /// <summary>Port UDP par défaut sur lequel un récepteur attend le flux audio.</summary>
    public const int DefaultAudioPort = 50506;

    public const int SampleRate = 48000;
    public const int Channels = 2;

    /// <summary>Durée d'une trame Opus. 10 ms est un bon compromis latence / surcoût réseau.</summary>
    public const int FrameDurationMs = 10;

    /// <summary>Nombre d'échantillons par canal dans une trame (480 à 48 kHz).</summary>
    public const int FrameSamplesPerChannel = SampleRate * FrameDurationMs / 1000;

    /// <summary>Nombre de valeurs float entrelacées dans une trame (960 en stéréo).</summary>
    public const int FrameInterleavedSamples = FrameSamplesPerChannel * Channels;

    /// <summary>Taille maximale d'une trame Opus encodée (largement suffisante pour 10 ms à 510 kb/s).</summary>
    public const int MaxOpusFrameBytes = 1276;

    public const int DefaultBitrate = 128_000;
}
