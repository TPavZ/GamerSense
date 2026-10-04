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

    public MainForm()
    {
        Text = "GamerSense v0.1.0";
        Width = 540;
        Height = 390;
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
        _engine.LevelChanged += level => BeginInvoke(() => _meter.Value = Math.Clamp((int)(level * 1000), 0, 1000));
        _engine.Faulted += message => BeginInvoke(() => SetStatus("Audio error: " + message));
        FormClosing += (_, _) =>
        {
            SaveDeviceSelections();
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
        }
    }

    private void SetStatus(string text) => _status.Text = text;
}
