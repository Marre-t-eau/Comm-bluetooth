using System.Net;
using System.Net.Sockets;
using CommBluetooth.Core.Network;
using CommBluetooth.Core.Protocol;
using CommBluetooth.Core.Streaming;
using NAudio.CoreAudioApi;

namespace CommBluetooth.Windows;

internal sealed class MainForm : Form
{
    private sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record QualityItem(int Bitrate, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly QualityItem[] Qualities =
    [
        new(96_000, "Économique (96 kb/s)"),
        new(128_000, "Standard (128 kb/s)"),
        new(192_000, "Haute (192 kb/s)"),
        new(256_000, "Maximale (256 kb/s)"),
    ];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ListBox _receiverList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _manualAddress = new() { Dock = DockStyle.Fill, PlaceholderText = "ex. 192.168.1.42" };
    private readonly ComboBox _deviceCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _qualityCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _startButton = new() { Text = "Diffuser", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly NotifyIcon _trayIcon = new() { Icon = SystemIcons.Application, Text = "Comm Bluetooth" };
    private readonly ToolStripMenuItem _trayToggle = new("Diffuser");
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    private ReceiverDiscovery? _discovery;
    private StreamingSession? _session;
    private LoopbackAudioSource? _source;
    private string _targetName = "";
    private long _lastBytes;
    private bool _trayHintShown;

    public MainForm()
    {
        Text = "Comm Bluetooth — son du PC vers le téléphone";
        Icon = SystemIcons.Application;
        MinimumSize = new Size(460, 420);
        Size = new Size(500, 460);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildTrayIcon();
        LoadDevices();

        _qualityCombo.Items.AddRange(Qualities);
        _qualityCombo.SelectedItem = Qualities.FirstOrDefault(q => q.Bitrate == _settings.Bitrate) ?? Qualities[1];
        _manualAddress.Text = _settings.ManualAddress ?? "";

        _receiverList.SelectedIndexChanged += (_, _) =>
        {
            if (_receiverList.SelectedItem is not null)
                _manualAddress.Text = "";
        };
        _receiverList.DoubleClick += (_, _) => { if (_session is null) StartStreaming(); };
        _qualityCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_session is not null && _qualityCombo.SelectedItem is QualityItem quality)
                _session.Bitrate = quality.Bitrate;
        };
        _startButton.Click += (_, _) => ToggleStreaming();
        _statsTimer.Tick += (_, _) => UpdateStatus();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

        StartDiscovery();
        UpdateStatus();
        _statsTimer.Start();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "Téléphones trouvés sur le réseau (ouvrez l'application Comm Bluetooth sur le téléphone) :",
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(3, 3, 3, 6),
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        layout.Controls.Add(_receiverList, 0, 1);
        layout.SetColumnSpan(_receiverList, 2);

        layout.Controls.Add(FieldLabel("Ou adresse IP :"), 0, 2);
        layout.Controls.Add(_manualAddress, 1, 2);
        layout.Controls.Add(FieldLabel("Son à envoyer :"), 0, 3);
        layout.Controls.Add(_deviceCombo, 1, 3);
        layout.Controls.Add(FieldLabel("Qualité :"), 0, 4);
        layout.Controls.Add(_qualityCombo, 1, 4);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_startButton, 1, 0);
        layout.Controls.Add(bottom, 0, 5);
        layout.SetColumnSpan(bottom, 2);

        Controls.Add(layout);
        AcceptButton = _startButton;
    }

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) };

    private void BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => RestoreFromTray());
        _trayToggle.Click += (_, _) => ToggleStreaming();
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.Visible = true;
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayIcon.ShowBalloonTip(3000, "Comm Bluetooth", "Toujours actif ici. Double-cliquez pour rouvrir.", ToolTipIcon.Info);
            _trayHintShown = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void LoadDevices()
    {
        _deviceCombo.Items.Add(new DeviceItem(null, "Sortie par défaut de Windows"));
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                _deviceCombo.Items.Add(new DeviceItem(device.ID, device.FriendlyName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible de lister les sorties audio : {ex.Message}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _deviceCombo.SelectedItem = _deviceCombo.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == _settings.DeviceId)
            ?? _deviceCombo.Items[0];
    }

    private void StartDiscovery()
    {
        try
        {
            _discovery = new ReceiverDiscovery();
            _discovery.ReceiversChanged += (_, _) =>
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(RefreshReceivers);
            };
        }
        catch (SocketException ex)
        {
            _receiverList.Items.Add($"Recherche automatique indisponible ({ex.Message}).");
            _receiverList.Items.Add("Saisissez l'adresse IP affichée sur le téléphone.");
            _receiverList.Enabled = false;
        }
    }

    private void RefreshReceivers()
    {
        if (_discovery is null)
            return;

        var selected = (_receiverList.SelectedItem as DiscoveredReceiver)?.AudioEndPoint.Address;
        var receivers = _discovery.Receivers;

        _receiverList.BeginUpdate();
        _receiverList.Items.Clear();
        foreach (var receiver in receivers)
            _receiverList.Items.Add(receiver);

        var toSelect = receivers.FirstOrDefault(r => r.AudioEndPoint.Address.Equals(selected));
        if (toSelect is null && string.IsNullOrWhiteSpace(_manualAddress.Text))
            toSelect = receivers.FirstOrDefault(r => r.Name == _settings.LastReceiverName) ?? (receivers.Count == 1 ? receivers[0] : null);
        if (toSelect is not null)
            _receiverList.SelectedItem = toSelect;
        _receiverList.EndUpdate();
    }

    private void ToggleStreaming()
    {
        if (_session is null)
            StartStreaming();
        else
            StopStreaming();
    }

    private void StartStreaming()
    {
        if (!TryGetTarget(out var target, out var name))
        {
            MessageBox.Show(this,
                "Choisissez un téléphone dans la liste ou saisissez son adresse IP.\n\n" +
                "Si la liste reste vide, vérifiez que l'application est ouverte sur le téléphone, " +
                "que les deux appareils sont sur le même Wi-Fi, et que le pare-feu Windows autorise Comm Bluetooth.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var quality = (QualityItem)_qualityCombo.SelectedItem!;
        var deviceItem = (DeviceItem)_deviceCombo.SelectedItem!;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = deviceItem.Id is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceItem.Id);

            _session = new StreamingSession(target, quality.Bitrate);
            _source = new LoopbackAudioSource(device, _session.Push);
            _source.Stopped += OnCaptureStopped;
            _source.Start();
        }
        catch (Exception ex)
        {
            StopStreaming();
            MessageBox.Show(this, $"Impossible de démarrer la diffusion : {ex.Message}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _targetName = name;
        _lastBytes = 0;
        _settings.DeviceId = deviceItem.Id;
        _settings.Bitrate = quality.Bitrate;
        _settings.ManualAddress = string.IsNullOrWhiteSpace(_manualAddress.Text) ? null : _manualAddress.Text.Trim();
        if (_receiverList.SelectedItem is DiscoveredReceiver receiver && _settings.ManualAddress is null)
            _settings.LastReceiverName = receiver.Name;
        _settings.Save();
        UpdateStatus();
    }

    private void OnCaptureStopped(object? sender, Exception? error)
    {
        if (error is null || !IsHandleCreated)
            return;

        BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _source))
                return;
            StopStreaming();
            MessageBox.Show(this,
                $"La capture du son s'est arrêtée : {error.Message}\n\n" +
                "Cela arrive si la sortie audio a été débranchée ou désactivée.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    private void StopStreaming()
    {
        _source?.Dispose();
        _source = null;
        _session?.Dispose();
        _session = null;
        UpdateStatus();
    }

    private bool TryGetTarget(out IPEndPoint target, out string name)
    {
        target = null!;
        name = "";
        var text = _manualAddress.Text.Trim();

        if (text.Length == 0)
        {
            if (_receiverList.SelectedItem is not DiscoveredReceiver receiver)
                return false;
            target = receiver.AudioEndPoint;
            name = receiver.Name;
            return true;
        }

        if (IPEndPoint.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            target = parsed.Port == 0 ? new IPEndPoint(parsed.Address, StreamFormat.DefaultAudioPort) : parsed;
            name = text;
            return true;
        }

        // Nom d'hôte (« mon-telephone.local »)
        try
        {
            var address = Dns.GetHostAddresses(text).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
                return false;
            target = new IPEndPoint(address, StreamFormat.DefaultAudioPort);
            name = text;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void UpdateStatus()
    {
        bool streaming = _session is not null;
        _startButton.Text = streaming ? "Arrêter" : "Diffuser";
        _trayToggle.Text = _startButton.Text;
        _receiverList.Enabled = !streaming && _discovery is not null;
        _manualAddress.Enabled = _deviceCombo.Enabled = !streaming;

        if (_session is null)
        {
            _status.Text = "Prêt.";
            _trayIcon.Text = "Comm Bluetooth — arrêté";
            return;
        }

        long bytes = _session.BytesSent;
        long kbps = _lastBytes == 0 ? 0 : (bytes - _lastBytes) * 8 / 1000;
        _lastBytes = bytes;

        string detail = _session.LastError is { } error ? $"Erreur réseau : {error}"
            : kbps < 8 ? "aucun son joué sur le PC"
            : $"{kbps} kb/s";
        _status.Text = $"Diffusion vers {_targetName} — {detail}";

        var tray = $"Comm Bluetooth — vers {_targetName}";
        _trayIcon.Text = tray.Length > 63 ? tray[..63] : tray;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statsTimer.Stop();
        StopStreaming();
        _discovery?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }
}
