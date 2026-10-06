using GamerSense.Audio;
using GamerSense.Settings;

namespace GamerSense;

public sealed class MainForm : Form
{
    private readonly AudioDeviceManager _devices = new();
    private readonly AudioEngine _engine = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ComboBox _input = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430 };
    private readonly ComboBox _output = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430 };
    private readonly Button _start = new() { Text = "START GAMERSENSE", Width = 200, Height = 42 };
    private readonly Button _refresh = new() { Text = "Refresh Devices", Width = 130 };
    private readonly ProgressBar _meter = new() { Width = 430, Maximum = 1000 };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready" };
    private bool _loadingDevices;
    private readonly SpectrumView _spectrum = new();
    private readonly Label _analysisText = new() { AutoSize = true, Text = "Analyzer ready — playback unchanged" };
    private readonly System.Windows.Forms.Timer _analysisTimer = new() { Interval = 50 };
    private readonly CheckBox _soundMatching = new() { Text = "Experimental Wardogs sound matching", Checked = true, AutoSize = true };
    private readonly Label _detectionText = new() { AutoSize = true, Text = "Sound matching idle" };
    private readonly Label _queueText = new() { AutoSize = true, Text = "Queued audio: 0 ms" };
    private readonly ComboBox _playbackMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 430 };
    private readonly Label _captureText = new() { AutoSize = true, Text = "Capture batch: 0 ms" };
    private readonly Button _copyAudioDetails = new() { Text = "Copy audio details", Width = 170 };
    private readonly EventReviewPanel _review;
    private bool _hotkeyRegistered;
    private System.Drawing.Icon? _appIcon;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    public MainForm()
    {
        Text = "GamerSense v0.4.15 — Event review";
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("GamerSense.AppIcon.ico"))
        {
            if (iconStream is not null) { _appIcon = new System.Drawing.Icon(iconStream); Icon = _appIcon; }
        }
        Width = 720;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 22);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var title = new Label { Text = "GAMERSENSE", Font = new Font("Segoe UI", 22f, FontStyle.Bold), AutoSize = true };
        var subtitle = new Label { Text = "Low-latency game audio processing", ForeColor = Color.Silver, AutoSize = true };
        var inputLabel = new Label { Text = "GAME AUDIO / VIRTUAL DEVICE", AutoSize = true };
        var outputLabel = new Label { Text = "TRUE OUTPUT", AutoSize = true };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(30),
            AutoScroll = true
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(new Label { Height = 12 });
        panel.Controls.Add(inputLabel);
        panel.Controls.Add(_input);
        panel.Controls.Add(outputLabel);
        panel.Controls.Add(_output);
        panel.Controls.Add(new Label { Text = "PLAYBACK MODE — change while stopped", AutoSize = true });
        _playbackMode.Items.AddRange(new object[] { "Stable playback", "Event-driven capture", "Minimum delay (test)", "Lean output (test)", "Low-period output (test)", "Direct refill (test)", "Direct cable capture (test)" });
        _playbackMode.SelectedIndex = _settings.DirectCableCapture ? 6 : _settings.RealTimeRefill ? 5 : _settings.LowEnginePeriod ? 4 : _settings.LeanOutput ? 3 : _settings.FastestLatency ? 2 : _settings.LowerLatency ? 1 : 0;
        _engine.LowerLatency = _settings.LowerLatency;
        _engine.FastestLatency = _settings.FastestLatency;
        _engine.LeanOutput = _settings.LeanOutput;
        _engine.LowEnginePeriod = _settings.LowEnginePeriod;
        _engine.RealTimeRefill = _settings.RealTimeRefill;
        _engine.DirectCableCapture = _settings.DirectCableCapture;
        _engine.SavedEvents.AutoSaveEnabled = _settings.AutoSaveEvents;
        panel.Controls.Add(_playbackMode);
        panel.Controls.Add(new Label { Text = "LIVE AUDIO", AutoSize = true });
        panel.Controls.Add(_meter);
        panel.Controls.Add(new Label { Text = "LIVE SPECTRUM • Hz / dBFS", AutoSize = true });
        panel.Controls.Add(_spectrum);
        panel.Controls.Add(_analysisText);
        panel.Controls.Add(_queueText);
        panel.Controls.Add(_captureText);
        panel.Controls.Add(_soundMatching);
        panel.Controls.Add(_detectionText);
        panel.Controls.Add(new Label { Text = "Weak matches show ambience/mixed audio. Audio unchanged.", AutoSize = true });

        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_refresh);
        panel.Controls.Add(buttons);
        panel.Controls.Add(_status);
        panel.Controls.Add(_copyAudioDetails);
        _review = new EventReviewPanel(() => _engine.Events, () => _engine.IsRunning,
            () => (_output.SelectedItem as AudioDeviceInfo)?.Id == (_input.SelectedItem as AudioDeviceInfo)?.Id
                ? null : (_output.SelectedItem as AudioDeviceInfo)?.Id, _engine.SavedEvents,
            enabled => { _settings.AutoSaveEvents = enabled; _settings.Save(); }, _settings.ApprovedExportDirectory,
            folder => { _settings.ApprovedExportDirectory = folder; _settings.Save(); });
        var tabs = new TabControl { Dock = DockStyle.Fill, ForeColor = Color.Black };
        var playback = new TabPage("Playback") { BackColor = BackColor, ForeColor = ForeColor };
        var review = new TabPage("Event review") { BackColor = BackColor, ForeColor = ForeColor };
        playback.Controls.Add(panel); review.Controls.Add(_review); tabs.TabPages.Add(playback); tabs.TabPages.Add(review); Controls.Add(tabs);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F8 && !e.Control && !e.Alt) { _review.MarkLive(); e.Handled = true; } };

        _refresh.Click += (_, _) => LoadDevices();
        _copyAudioDetails.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_engine.AudioDetails + $"\nInput: {_input.Text}\nOutput: {_output.Text}");
                SetStatus("Audio details copied, including the last playback session.");
            }
            catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard busy; try again."); }
        };
        _start.Click += (_, _) => ToggleEngine();
        _input.SelectedIndexChanged += (_, _) => SaveDeviceSelections();
        _output.SelectedIndexChanged += (_, _) => SaveDeviceSelections();
        _soundMatching.CheckedChanged += (_, _) => _engine.DetectionEnabled = _soundMatching.Checked;
        _playbackMode.SelectedIndexChanged += (_, _) =>
        {
            SaveDeviceSelections();
            _settings.DirectCableCapture = _playbackMode.SelectedIndex == 6;
            _settings.LowerLatency = _playbackMode.SelectedIndex >= 1;
            _settings.FastestLatency = _playbackMode.SelectedIndex == 2;
            _settings.LeanOutput = _playbackMode.SelectedIndex == 3;
            _settings.LowEnginePeriod = _playbackMode.SelectedIndex == 4;
            _settings.RealTimeRefill = _playbackMode.SelectedIndex == 5;
            _engine.LowerLatency = _settings.LowerLatency;
            _engine.FastestLatency = _settings.FastestLatency;
            _engine.LeanOutput = _settings.LeanOutput;
            _engine.LowEnginePeriod = _settings.LowEnginePeriod;
            _engine.RealTimeRefill = _settings.RealTimeRefill;
            _engine.DirectCableCapture = _settings.DirectCableCapture;
            LoadDevices();
            _settings.Save();
        };
        _analysisTimer.Tick += (_, _) =>
        {
            var frame = _engine.Analysis;
            _spectrum.Frame = frame;
            _spectrum.Invalidate();
            _detectionText.Text = _engine.Detection;
            _queueText.Text = $"Queued audio: {_engine.QueuedAudioMs:F0} ms";
            _captureText.Text = $"Capture batch: {_engine.CaptureBatchMs:F0} ms";
            _meter.Value = frame is null ? 0 : Math.Clamp((int)(Math.Pow(10, frame.PeakDb / 20) * 1000), 0, 1000);
            _analysisText.Text = frame is null ? "Analyzer idle — playback unchanged" : !frame.Supported ? "Analysis unavailable for this format; playback continues" : $"Peak {frame.PeakDb:F1} | RMS {frame.RmsDb:F1} dBFS | Dominant {frame.DominantHz:F0} Hz";
        };
        _analysisTimer.Start();
        _engine.Faulted += ReportFault;
        FormClosing += (_, _) =>
        {
            SaveDeviceSelections();
            _analysisTimer.Stop();
            _analysisTimer.Dispose();
            _review.StopReplay();
            if (_hotkeyRegistered) UnregisterHotKey(Handle, 4108);
            _engine.Dispose();
        };
        Shown += (_, _) =>
        {
            LoadDevices();
            _hotkeyRegistered = RegisterHotKey(Handle, 4108, 0x4003, (uint)Keys.F8);
            if (!_hotkeyRegistered) SetStatus("Global mark shortcut unavailable; use the Mark moment button or F8 in this app.");
        };
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == 4108) { _review?.MarkLive(); return; }
        base.WndProc(ref message);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _appIcon?.Dispose(); _appIcon = null; }
        base.Dispose(disposing);
    }

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            var outputs = _devices.GetActiveRenderDevices().ToList();
            var devices = _settings.DirectCableCapture ? _devices.GetActiveCaptureDevices().ToList() : outputs;
            _input.DataSource = devices.ToList();
            _output.DataSource = outputs;
            _input.DisplayMember = nameof(AudioDeviceInfo.Name);
            _output.DisplayMember = nameof(AudioDeviceInfo.Name);

            var savedInputIndex = devices.FindIndex(d => d.Id == (_settings.DirectCableCapture ? _settings.DirectCaptureDeviceId : _settings.InputDeviceId));
            var savedOutputIndex = outputs.FindIndex(d => d.Id == _settings.OutputDeviceId);

            if (savedInputIndex >= 0)
            {
                _input.SelectedIndex = savedInputIndex;
            }
            else
            {
                var cable = _settings.DirectCableCapture ? devices.FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)) : _devices.FindVirtualCable();
                if (cable is not null)
                {
                    var cableIndex = devices.FindIndex(d => d.Id == cable.Id);
                    if (cableIndex >= 0) _input.SelectedIndex = cableIndex;
                }
            }

            if (savedOutputIndex >= 0)
                _output.SelectedIndex = savedOutputIndex;

            if (_settings.DirectCableCapture)
                SetStatus("Direct cable test: choose CABLE Output; keep game/player output on CABLE Input. Turn Windows Listen off.");
            else if (_devices.FindVirtualCable() is not null)
                SetStatus("Virtual audio device detected. Device selections are remembered automatically.");
            else
                SetStatus("No VB-CABLE playback device detected yet.");
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    private void SaveDeviceSelections()
    {
        if (_loadingDevices)
            return;

        if (_input.SelectedItem is AudioDeviceInfo input)
        {
            if (_settings.DirectCableCapture) _settings.DirectCaptureDeviceId = input.Id;
            else _settings.InputDeviceId = input.Id;
        }
        if (_output.SelectedItem is AudioDeviceInfo output)
            _settings.OutputDeviceId = output.Id;

        _settings.Save();
    }

    private void ToggleEngine()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            _start.Text = "START GAMERSENSE";
            _playbackMode.Enabled = true;
            SetStatus("Stopped");
            return;
        }

        if (_input.SelectedItem is not AudioDeviceInfo input || _output.SelectedItem is not AudioDeviceInfo output)
            return;
        if (input.Id == output.Id)
        {
            MessageBox.Show("The virtual game-audio device and true output must be different.", "GamerSense");
            return;
        }

        SaveDeviceSelections();

        try
        {
            _review.StopReplay();
            _engine.Start(input.Id, output.Id);
            _start.Text = "STOP GAMERSENSE";
            _playbackMode.Enabled = false;
            SetStatus("Processing audio → " + output.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to start GamerSense", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Start failed");
            _engine.Stop();
        }
    }

    private void SetStatus(string text) => _status.Text = text;
    private void ReportFault(string message)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => SetStatus("Audio error: " + message)); }
        catch (InvalidOperationException) { }
    }
}

