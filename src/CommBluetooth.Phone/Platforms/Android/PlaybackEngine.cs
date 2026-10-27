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

    /// <summary>Latence de bout en bout côté téléphone (tampon réseau + tampon audio), en ms.</summary>
    public int LatencyMs
    {
        get
        {
            var receiver = _receiver;
            var track = _track;
            if (receiver is null || track is null)
                return 0;
            int bufferMs = receiver.Buffer.BufferedFrames * StreamFormat.FrameDurationMs;
            int trackMs = track.BufferSizeInFrames * 1000 / StreamFormat.SampleRate;
            return bufferMs + trackMs;
        }
    }

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
                _track = CreateTrack();
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
        AndroidProcess.SetThreadPriority(ThreadPriority.UrgentAudio);
        var frame = new float[StreamFormat.FrameInterleavedSamples];
        var receiver = _receiver!;
        var track = _track!;

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
        }
    }

    private static AudioTrack CreateTrack()
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

        // Le tampon d'AudioTrack s'ajoute à la latence : on prend le minimum du système,
        // avec au moins 4 trames de 10 ms pour absorber les petits retards du thread.
        int minBytes = AudioTrack.GetMinBufferSize(StreamFormat.SampleRate, ChannelOut.Stereo, Encoding.PcmFloat);
        int bytes = Math.Max(minBytes, StreamFormat.FrameInterleavedSamples * sizeof(float) * 4);

        return new AudioTrack.Builder()
            .SetAudioAttributes(attributes)!
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(bytes)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!
            .Build()!;
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
        var mode = OperatingSystem.IsAndroidVersionAtLeast(29) ? WifiMode.FullLowLatency : WifiMode.FullHighPerf;
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
        AudioDeviceType.BleHeadset or AudioDeviceType.BleSpeaker or AudioDeviceType.BleBroadcast => "Bluetooth LE",
        AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset => "filaire",
        AudioDeviceType.UsbHeadset or AudioDeviceType.UsbDevice => "USB",
        AudioDeviceType.BuiltinSpeaker => "haut-parleur du téléphone",
        _ => type.ToString(),
    };
}
