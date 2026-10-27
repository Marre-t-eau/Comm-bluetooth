using Android.Content;
using Android.Media;
using Android.Net.Wifi;
using Android.OS;
using CommBluetooth.Core.Protocol;
using CommBluetooth.Core.Receiving;
using AndroidProcess = Android.OS.Process;

namespace CommBluetooth.Phone;

/// <summary>
/// Cœur de l'application : reçoit le flux du PC (via <see cref="StreamReceiver"/>) et le joue
/// avec un AudioTrack. Android route automatiquement ce son vers les écouteurs Bluetooth connectés.
/// Une seule instance, pilotée par <see cref="StreamingService"/> ; l'interface ne fait que la lire.
/// </summary>
public sealed class PlaybackEngine
{
    public static PlaybackEngine Instance { get; } = new();

    private readonly Lock _gate = new();
    private StreamReceiver? _receiver;
    private AudioTrack? _track;
    private Thread? _thread;
    private WifiManager.WifiLock? _wifiLock;
    private volatile bool _running;
    private long _framesWritten;
    private int _burstFrames = StreamFormat.FrameSamplesPerChannel;

    private PlaybackEngine() { }

    public bool IsRunning => _running;
    public string? Error { get; private set; }
    public System.Net.IPAddress? Sender => _receiver?.Sender;
    public JitterBuffer? Buffer => _receiver?.Buffer;

    /// <summary>Nom de la sortie audio réellement utilisée (par exemple « Galaxy Buds2 »).</summary>
    public string? OutputDeviceName
    {
        get
        {
            var device = _track?.RoutedDevice;
            return device is null ? null : $"{device.ProductName} ({DescribeType(device.Type)})";
        }
    }

    /// <summary>Latence ajoutée par le tampon réseau, en ms.</summary>
    public int NetworkBufferMs => (_receiver?.Buffer.BufferedFrames ?? 0) * StreamFormat.FrameDurationMs;

    /// <summary>
    /// Latence de sortie mesurée par Android : du moment où l'on écrit le son jusqu'à ce qu'il soit
    /// joué. Sur la plupart des téléphones elle inclut le Bluetooth ; sinon c'est le tampon d'AudioTrack.
    /// </summary>
    public int OutputLatencyMs
    {
        get
        {
            var track = _track;
            if (track is null)
                return 0;

            using var timestamp = new AudioTimestamp();
            if (track.GetTimestamp(timestamp))
            {
                long pending = Interlocked.Read(ref _framesWritten) - timestamp.FramePosition;
                double sinceTimestampMs = (Java.Lang.JavaSystem.NanoTime() - timestamp.NanoTime) / 1e6;
                double ms = pending * 1000.0 / StreamFormat.SampleRate - sinceTimestampMs;
                if (ms > 0)
                    return (int)ms;
            }
            return track.BufferSizeInFrames * 1000 / StreamFormat.SampleRate;
        }
    }

    /// <summary>Latence totale côté téléphone (tampon réseau + sortie), en ms.</summary>
    public int LatencyMs => _track is null ? 0 : NetworkBufferMs + OutputLatencyMs;

    public void Start(Context context, string name, int targetFrames)
    {
        lock (_gate)
        {
            if (_running)
            {
                _receiver!.Buffer.TargetFrames = targetFrames;
                return;
            }

            Error = null;
            try
            {
                _receiver = new StreamReceiver(name, targetFrames);
                _track = CreateTrack(context);
                Interlocked.Exchange(ref _framesWritten, 0);
            }
            catch (Exception ex)
            {
                Error = ex is System.Net.Sockets.SocketException
                    ? $"Le port {StreamFormat.DefaultAudioPort} est déjà utilisé : {ex.Message}"
                    : ex.Message;
                ReleaseLocked();
                throw;
            }

            AcquireWifiLock(context);
            _running = true;
            _track.Play();
            _thread = new Thread(PlaybackLoop) { IsBackground = true, Name = "Lecture audio" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            if (!_running)
                return;
            _running = false;
            thread = _thread;
        }

        thread?.Join(TimeSpan.FromSeconds(1));
        lock (_gate)
            ReleaseLocked();
    }

    private void PlaybackLoop()
    {
        AndroidProcess.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var frame = new float[StreamFormat.FrameInterleavedSamples];
        var receiver = _receiver!;
        var track = _track!;
        int underruns = track.UnderrunCount;
        int writes = 0;

        while (_running)
        {
            // Sans son à jouer, ReadFrame fournit du silence : on l'écrit quand même pour que
            // l'écriture bloquante garde le rythme (10 ms par trame) et que la sortie reste ouverte.
            receiver.ReadFrame(frame);
            int written = track.Write(frame, 0, frame.Length, WriteMode.Blocking);
            if (written < 0)
            {
                Error = $"Erreur de lecture audio ({written}).";
                break;
            }
            Interlocked.Add(ref _framesWritten, written / StreamFormat.Channels);

            // Tampon de sortie volontairement petit : s'il se vide (coupure audible), on l'agrandit
            // d'un cran, jusqu'à trouver la taille la plus basse qui tient sur ce téléphone.
            if (++writes % 50 == 0 && track.UnderrunCount > underruns)
            {
                underruns = track.UnderrunCount;
                int size = track.BufferSizeInFrames + _burstFrames;
                if (size <= track.BufferCapacityInFrames)
                    track.SetBufferSizeInFrames(size);
            }
        }
    }

    private AudioTrack CreateTrack(Context context)
    {
        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!;
        var format = new AudioFormat.Builder()
            .SetSampleRate(StreamFormat.SampleRate)!
            .SetEncoding(Encoding.PcmFloat)!
            .SetChannelMask(ChannelOut.Stereo)!
            .Build()!;

        // L'écriture bloquante garde le tampon d'AudioTrack toujours plein : toute sa taille s'ajoute
        // à la latence. On alloue large (pour pouvoir l'agrandir si besoin) mais on n'en utilise
        // au départ que deux « rafales » du mixeur Android, et au moins 20 ms.
        var audioManager = context.GetSystemService(Context.AudioService) as AudioManager;
        if (int.TryParse(audioManager?.GetProperty(AudioManager.PropertyOutputFramesPerBuffer), out int burst) && burst > 0)
            _burstFrames = burst;

        int minBytes = AudioTrack.GetMinBufferSize(StreamFormat.SampleRate, ChannelOut.Stereo, Encoding.PcmFloat);
        int bytes = Math.Max(minBytes, StreamFormat.FrameInterleavedSamples * sizeof(float) * 10);

        var track = new AudioTrack.Builder()
            .SetAudioAttributes(attributes)!
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(bytes)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!
            .Build()!;

        int startFrames = Math.Max(2 * _burstFrames, 2 * StreamFormat.FrameSamplesPerChannel);
        track.SetBufferSizeInFrames(Math.Min(startFrames, track.BufferCapacityInFrames));
        return track;
    }

    /// <summary>
    /// Sans ce verrou, le Wi-Fi du téléphone passe en économie d'énergie écran éteint et
    /// regroupe les paquets : le son se met alors à hacher.
    /// </summary>
    private void AcquireWifiLock(Context context)
    {
        if (context.ApplicationContext?.GetSystemService(Context.WifiService) is not WifiManager wifi)
            return;

#pragma warning disable CA1422 // FullHighPerf est remplacé par FullLowLatency à partir d'Android 10
        var mode = OperatingSystem.IsAndroidVersionAtLeast(29) ? Android.Net.WifiMode.FullLowLatency : Android.Net.WifiMode.FullHighPerf;
#pragma warning restore CA1422
        _wifiLock = wifi.CreateWifiLock(mode, "CommBluetooth");
        _wifiLock?.SetReferenceCounted(false);
        _wifiLock?.Acquire();
    }

    private void ReleaseLocked()
    {
        _thread = null;
        _receiver?.Dispose();
        _receiver = null;
        if (_track is not null)
        {
            try
            {
                _track.Stop();
            }
            catch (Java.Lang.IllegalStateException) { }
            _track.Release();
            _track = null;
        }
        if (_wifiLock?.IsHeld == true)
            _wifiLock.Release();
        _wifiLock = null;
    }

    private static string DescribeType(AudioDeviceType type) => type switch
    {
        AudioDeviceType.BluetoothA2dp => "Bluetooth",
        AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset => "filaire",
        AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice => "USB",
        AudioDeviceType.BuiltinSpeaker => "haut-parleur du téléphone",
        // Les types BLE (Android 12+) sont reconnus par leur nom pour rester compatible Android 8.
        _ when type.ToString().StartsWith("Ble", StringComparison.Ordinal) => "Bluetooth LE",
        _ => type.ToString(),
    };
}
