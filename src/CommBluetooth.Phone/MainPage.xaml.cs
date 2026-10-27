namespace CommBluetooth.Phone;

public partial class MainPage : ContentPage
{
    private sealed record LatencyChoice(string Label, int TargetFrames)
    {
        public override string ToString() => Label;
    }

    private const string LatencyPreference = "latencyIndex";
    private const string KeepScreenOnPreference = "keepScreenOn";

    private static readonly LatencyChoice[] Choices =
    [
        new("Faible (≈ 30 ms)", 3),
        new("Normale (≈ 50 ms)", 5),
        new("Stable (≈ 100 ms)", 10),
    ];

    private IDispatcherTimer? _timer;
    private long _lastPlayed;
    private long _lastConcealed;

    public MainPage()
    {
        InitializeComponent();
        LatencyPicker.ItemsSource = Choices;
        LatencyPicker.SelectedIndex = Math.Clamp(Preferences.Get(LatencyPreference, 1), 0, Choices.Length - 1);
        KeepScreenOnSwitch.IsToggled = Preferences.Get(KeepScreenOnPreference, false);
    }

    private void OnKeepScreenOnToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Set(KeepScreenOnPreference, e.Value);
        DeviceDisplay.Current.KeepScreenOn = e.Value;
    }

    private LatencyChoice SelectedLatency => (LatencyChoice?)LatencyPicker.SelectedItem ?? Choices[1];

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var context = Platform.AppContext;
        NameLabel.Text = DeviceName.Get(context);
        AddressLabel.Text = DeviceName.WifiAddress() ?? "pas de Wi-Fi";

        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
        }
    }

    private async void OnToggleClicked(object? sender, EventArgs e)
    {
        var context = Platform.AppContext;
        if (PlaybackEngine.Instance.IsRunning)
        {
            StreamingService.Stop(context);
        }
        else
        {
            // Sans cette autorisation (Android 13+), la notification d'arrêt n'apparaît pas,
            // mais la réception fonctionne quand même : on ne bloque pas sur un refus.
            await Permissions.RequestAsync<Permissions.PostNotifications>();
            StreamingService.Start(context, SelectedLatency.TargetFrames);
        }

        // Le service démarre de façon asynchrone : on rafraîchit un peu plus tard aussi.
        Refresh();
        await Task.Delay(300);
        Refresh();
    }

    private void OnLatencyChanged(object? sender, EventArgs e)
    {
        Preferences.Set(LatencyPreference, LatencyPicker.SelectedIndex);
        if (PlaybackEngine.Instance.Buffer is { } buffer)
            buffer.TargetFrames = SelectedLatency.TargetFrames;
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var engine = PlaybackEngine.Instance;
        ToggleButton.Text = engine.IsRunning ? "Arrêter" : "Démarrer";

        if (!engine.IsRunning)
        {
            StatusLabel.Text = engine.Error is null ? "Arrêté" : "Erreur";
            StatusDetailLabel.Text = engine.Error ?? "Appuyez sur Démarrer pour que le PC trouve ce téléphone.";
            OutputLabel.Text = "—";
            LatencyLabel.Text = "—";
            DiagnosticLabel.Text = "—";
            return;
        }

        OutputLabel.Text = engine.OutputDeviceName ?? "—";
        var buffer = engine.Buffer;
        if (engine.Sender is not { } pc || buffer is null)
        {
            StatusLabel.Text = "En attente du PC…";
            StatusDetailLabel.Text = "Lancez la diffusion depuis Comm Bluetooth sur le PC.";
            LatencyLabel.Text = "—";
            DiagnosticLabel.Text = "—";
            return;
        }

        long played = buffer.FramesPlayed;
        long concealed = buffer.FramesConcealed;
        long playedDelta = played - _lastPlayed;
        long concealedDelta = concealed - _lastConcealed;
        _lastPlayed = played;
        _lastConcealed = concealed;

        StatusLabel.Text = "Son reçu";
        string loss = playedDelta > 0 && concealedDelta > 0
            ? $" — {concealedDelta * 100.0 / playedDelta:0.#} % de pertes masquées"
            : "";
        StatusDetailLabel.Text = $"Depuis le PC {pc}{loss}";
        int network = engine.NetworkBufferMs;
        int output = engine.OutputLatencyMs;
        LatencyLabel.Text = $"≈ {network + output} ms (tampon {network} ms + sortie {output} ms)";

        // Sortie = tampon Android + Bluetooth : la différence est le délai propre aux écouteurs.
        int android = engine.AndroidBufferMs;
        int margin = (buffer.EffectiveTargetFrames - buffer.TargetFrames) * Core.Protocol.StreamFormat.FrameDurationMs;
        DiagnosticLabel.Text =
            $"Paquets espacés jusqu'à {buffer.MaxArrivalGapMs} ms (idéal : 10)\n" +
            $"Marge auto +{margin} ms · tampon Android {android} ms · Bluetooth ≈ {Math.Max(0, output - android)} ms";
    }
}
