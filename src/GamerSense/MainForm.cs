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

    public MainForm()
    {
        Text = "GamerSense v0.3.0 — Experimental";
        Width = 540;
        Height = 760;
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
        panel.Controls.Add(new Label { Text = "LIVE AUDIO", AutoSize = true });
        panel.Controls.Add(_meter);
        panel.Controls.Add(new Label { Text = "LIVE SPECTRUM • Hz / dBFS", AutoSize = true });
        panel.Controls.Add(_spectrum);
        panel.Controls.Add(_analysisText);
        panel.Controls.Add(_soundMatching);
        panel.Controls.Add(_detectionText);
        panel.Controls.Add(new Label { Text = "Closest learned pattern; may be wrong. Audio unchanged.", AutoSize = true });

        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_refresh);
        panel.Controls.Add(buttons);
        panel.Controls.Add(_status);
        Controls.Add(panel);

        _refresh.Click += (_, _) => LoadDevices();
        _start.Click += (_, _) => ToggleEngine();
        _input.SelectedIndexChanged += (_, _) => SaveDeviceSelections();
        _output.SelectedIndexChanged += (_, _) => SaveDeviceSelections();
        _soundMatching.CheckedChanged += (_, _) => _engine.DetectionEnabled = _soundMatching.Checked;
        _analysisTimer.Tick += (_, _) =>
        {
            var frame = _engine.Analysis;
            _spectrum.Frame = frame;
            _spectrum.Invalidate();
            _detectionText.Text = _engine.Detection;
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
            _engine.Dispose();
        };
        Shown += (_, _) => LoadDevices();
    }

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            var devices = _devices.GetActiveRenderDevices().ToList();
            _input.DataSource = devices.ToList();
            _output.DataSource = devices.ToList();
            _input.DisplayMember = nameof(AudioDeviceInfo.Name);
            _output.DisplayMember = nameof(AudioDeviceInfo.Name);

            var savedInputIndex = devices.FindIndex(d => d.Id == _settings.InputDeviceId);
            var savedOutputIndex = devices.FindIndex(d => d.Id == _settings.OutputDeviceId);

            if (savedInputIndex >= 0)
            {
                _input.SelectedIndex = savedInputIndex;
            }
            else
            {
                var cable = _devices.FindVirtualCable();
                if (cable is not null)
                {
                    var cableIndex = devices.FindIndex(d => d.Id == cable.Id);
                    if (cableIndex >= 0) _input.SelectedIndex = cableIndex;
                }
            }

            if (savedOutputIndex >= 0)
                _output.SelectedIndex = savedOutputIndex;

            if (_devices.FindVirtualCable() is not null)
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
            _settings.InputDeviceId = input.Id;
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
            _engine.Start(input.Id, output.Id);
            _start.Text = "STOP GAMERSENSE";
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
