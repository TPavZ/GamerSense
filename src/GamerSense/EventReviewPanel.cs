using GamerSense.Audio;
using NAudio.Wave;
using NAudio.CoreAudioApi;
namespace GamerSense;

public sealed class EventReviewPanel : FlowLayoutPanel
{
    private readonly Func<EventMonitor?> _live;
    private readonly Func<bool> _running;
    private readonly Func<string?> _outputId;
    private EventMonitor? _offline;
    private EventClip? _clip;
    private WasapiOut? _player;
    private RawSourceWaveStream? _stream;
    private MMDeviceEnumerator? _devices;
    private readonly AudioTimeline _timeline = new();
    private readonly ClipWaveform _waveform = new();
    private readonly SpectrumView _clipSpectrum = new() { Width = 600, Height = 140 };
    private int _rangeGeneration;
    private readonly ListBox _events = new() { Width = 600, Height = 120, DisplayMember = nameof(MarkerRow.Text) };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(610, 0), Text = "Start Stable playback to monitor, or open a sample while stopped." };
    private readonly NumericUpDown _before = new() { Minimum = 0, Maximum = 10, Value = 2, DecimalPlaces = 1, Increment = .5m, Width = 75 };
    private readonly NumericUpDown _after = new() { Minimum = 0, Maximum = 10, Value = 3, DecimalPlaces = 1, Increment = .5m, Width = 75 };
    private readonly NumericUpDown _trimStart = new() { Minimum = 0, Maximum = 20, DecimalPlaces = 2, Increment = .1m, Width = 90 };
    private readonly NumericUpDown _trimEnd = new() { Minimum = 0, Maximum = 20, DecimalPlaces = 2, Increment = .1m, Width = 90 };
    private readonly NumericUpDown _threshold = new() { Minimum = -60, Maximum = 0, Value = -18, Width = 75 };
    private readonly ComboBox _label = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _intent = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox _notes = new() { Width = 600, PlaceholderText = "What is audible? Distance, overlap, weapon/vehicle type…" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private int _lastId = -1;
    private EventMonitor? _shown;
    private bool _rebuilding;
    private int _lastSecond = -1;
    private sealed record MarkerRow(AudioMarker Marker, bool Expired = false)
    {
        public string Text => $"{Marker.Seconds:F1}s • {Marker.Kind}" + (Marker.PeakDb > -100 ? $" • {Marker.PeakDb:F1} dBFS" : "") + (Expired ? " • expired" : "");
    }
    private EventMonitor? Monitor => _offline ?? _live();
    public EventReviewPanel(Func<EventMonitor?> live, Func<bool> running, Func<string?> outputId)
    {
        _live = live; _running = running; _outputId = outputId;
        Dock = DockStyle.Fill; FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true; Padding = new Padding(20);
        _events.BackColor = Color.FromArgb(30, 34, 42); _events.ForeColor = Color.White;
        foreach (var control in new Control[] { _before, _after, _trimStart, _trimEnd, _threshold, _label, _intent, _notes })
            control.ForeColor = Color.Black;
        Controls.Add(new Label { Text = "LIVE EVENT REVIEW — latest 60 seconds", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Markers highlight activity, not verified sound types. Playback stays unchanged.", AutoSize = true });
        var buttons = Row();
        Button Add(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); buttons.Controls.Add(b); return b; }
        Add("Mark moment (Ctrl+Alt+F8)", () => { if (Monitor is null || !Monitor.Supported) throw new InvalidOperationException("Start monitoring with a supported audio format first."); Monitor.Mark(); RefreshTimeline(); });
        Add("Open audio sample", OpenSample);
        Add("Show live session", () => { _offline = null; _clip = null; _lastId = -1; RefreshTimeline(); });
        Controls.Add(buttons);
        var threshold = Row(); threshold.Controls.Add(new Label { Text = "Loud peak threshold (dBFS)", AutoSize = true }); threshold.Controls.Add(_threshold); Controls.Add(threshold);
        Controls.Add(_timeline); Controls.Add(_events);
        var window = Row(); window.Controls.Add(new Label { Text = "Seconds before / after event", AutoSize = true }); window.Controls.Add(_before); window.Controls.Add(_after);
        var load = new Button { Text = "Load / refresh event clip", AutoSize = true, ForeColor = Color.Black }; load.Click += (_, _) => Guard(LoadClip); window.Controls.Add(load); Controls.Add(window);
        var trim = Row(); trim.Controls.Add(new Label { Text = "Review range within clip (seconds)", AutoSize = true }); trim.Controls.Add(_trimStart); trim.Controls.Add(_trimEnd);
        var replay = new Button { Text = "Replay range", AutoSize = true, ForeColor = Color.Black }; replay.Click += (_, _) => Guard(Replay); trim.Controls.Add(replay);
        var stop = new Button { Text = "Stop replay", AutoSize = true, ForeColor = Color.Black }; stop.Click += (_, _) => StopReplay(); trim.Controls.Add(stop); Controls.Add(trim);
        Controls.Add(_waveform);
        Controls.Add(new Label { Text = "Range waveform above; spectrum samples the end of the range", AutoSize = true });
        Controls.Add(_clipSpectrum);
        var review = Row();
        _label.Items.AddRange(new object[] { "movement", "nearby footsteps", "own footsteps", "gunfire", "reload", "explosions / mortars", "air vehicle", "ground vehicle", "building / repair", "horn / alarm", "speech", "ambience", "mixed / uncertain" }); _label.SelectedIndex = 12;
        _intent.Items.AddRange(new object[] { "Unsure", "Keep", "Reduce" }); _intent.SelectedIndex = 0;
        review.Controls.Add(_label); review.Controls.Add(_intent);
        var save = new Button { Text = "Save WAV + label", AutoSize = true, ForeColor = Color.Black }; save.Click += (_, _) => Guard(Save); review.Controls.Add(save); Controls.Add(review); Controls.Add(_notes); Controls.Add(_info);
        _events.SelectedIndexChanged += (_, _) => { if (!_rebuilding) Guard(LoadClip); };
        _timeline.MarkerSelected += marker => { for (int i = 0; i < _events.Items.Count; i++) if (((MarkerRow)_events.Items[i]).Marker.Id == marker.Id) { _events.SelectedIndex = i; break; } };
        _threshold.ValueChanged += (_, _) => { if (Monitor is not null) Monitor.PeakThresholdDb = (double)_threshold.Value; };
        _trimStart.ValueChanged += (_, _) => UpdateRange(); _trimEnd.ValueChanged += (_, _) => UpdateRange();
        _timer.Tick += (_, _) => RefreshTimeline(); _timer.Start();
    }
    private static FlowLayoutPanel Row() => new() { AutoSize = true, MaximumSize = new Size(640, 0), WrapContents = true };
    private void Guard(Action action) { try { action(); } catch (Exception ex) { _info.Text = ex.Message; } }
    public void MarkLive()
    {
        Guard(() => { if (_live() is null || !_live()!.Supported || !_running()) throw new InvalidOperationException("Start playback with a supported audio format to mark a live event."); _live()!.Mark(); });
    }
    private void RefreshTimeline()
    {
        var monitor = Monitor; if (monitor is null) return;
        monitor.PeakThresholdDb = (double)_threshold.Value;
        var markers = monitor.Markers();
        _timeline.Points = monitor.Levels(); _timeline.Markers = markers;
        _timeline.EndSeconds = Math.Max(1, monitor.Seconds); _timeline.StartSeconds = Math.Max(0, monitor.Seconds - 60); _timeline.Invalidate();
        int id = markers.LastOrDefault()?.Id ?? 0;
        if (_shown != monitor || id != _lastId || (int)monitor.Seconds != _lastSecond)
        {
            int? selected = _shown == monitor ? (_events.SelectedItem as MarkerRow)?.Marker.Id : null;
            _rebuilding = true;
            _events.BeginUpdate(); _events.Items.Clear(); foreach (var marker in markers) _events.Items.Add(new MarkerRow(marker,
                Math.Max(0, marker.Seconds - (double)_before.Value) < Math.Max(0, monitor.Seconds - 60)));
            _shown = monitor; _lastId = id; _lastSecond = (int)monitor.Seconds;
            if (selected is not null) for (int i = 0; i < _events.Items.Count; i++) if (((MarkerRow)_events.Items[i]).Marker.Id == selected) _events.SelectedIndex = i;
            _events.EndUpdate();
            _rebuilding = false;
        }
    }
    private void LoadClip()
    {
        if (_events.SelectedItem is not MarkerRow selected || Monitor is null) return;
        _clip = null; _waveform.Clip = null; _clipSpectrum.Frame = null; _rangeGeneration++;
        _waveform.Invalidate(); _clipSpectrum.Invalidate();
        _clip = Monitor.Extract(selected.Marker, (double)_before.Value, (double)_after.Value);
        _trimStart.Value = 0; _trimEnd.Value = Math.Min(_trimEnd.Maximum, (decimal)(_clip.EndSeconds - _clip.StartSeconds));
        _info.Text = $"Frozen clip {_clip.StartSeconds:F2}–{_clip.EndSeconds:F2}s. Adjust the range to focus your review. Recent events may need refreshing after post-event audio arrives.";
        UpdateRange();
    }
    private async void UpdateRange()
    {
        int generation = ++_rangeGeneration;
        if (_clip is null) return;
        _waveform.Clip = _clip; _waveform.RangeStart = (double)_trimStart.Value; _waveform.RangeEnd = (double)_trimEnd.Value; _waveform.Invalidate();
        try
        {
            var clip = Range(); using var analyzer = new LiveAnalyzer(clip.Format);
            analyzer.Tap(clip.Audio, clip.Audio.Length); await Task.Delay(100);
            if (!IsDisposed && generation == _rangeGeneration) { _clipSpectrum.Frame = analyzer.Latest; _clipSpectrum.Invalidate(); }
        }
        catch (Exception ex) { if (!IsDisposed && generation == _rangeGeneration) _info.Text = ex.Message; }
    }
    private EventClip Range()
    {
        if (_clip is null) throw new InvalidOperationException("Select an event first.");
        double duration = _clip.EndSeconds - _clip.StartSeconds, start = (double)_trimStart.Value, end = (double)_trimEnd.Value;
        if (end <= start || end > duration + .01) throw new InvalidOperationException("Choose a valid review range within the loaded clip.");
        int first = (int)(start * _clip.Format.SampleRate) * _clip.Format.BlockAlign;
        int last = Math.Min(_clip.Audio.Length, (int)(end * _clip.Format.SampleRate) * _clip.Format.BlockAlign);
        if (last <= first) throw new InvalidOperationException("Review range is empty.");
        var data = new byte[last - first]; Buffer.BlockCopy(_clip.Audio, first, data, 0, data.Length);
        return _clip with { Audio = data, StartSeconds = _clip.StartSeconds + first / (double)_clip.Format.AverageBytesPerSecond, EndSeconds = _clip.StartSeconds + last / (double)_clip.Format.AverageBytesPerSecond };
    }
    private void Replay()
    {
        if (_running()) throw new InvalidOperationException("Stop live playback before replaying a clip.");
        var clip = Range(); string output = _outputId() ?? throw new InvalidOperationException("Select your true output on the Playback tab first.");
        StopReplay();
        _devices = new MMDeviceEnumerator(); _player = new WasapiOut(_devices.GetDevice(output), AudioClientShareMode.Shared, true, 30);
        _stream = new RawSourceWaveStream(new MemoryStream(clip.Audio, false), clip.Format); _player.Init(_stream); _player.Play();
        _info.Text = "Replaying the selected range through your true output.";
    }
    public void StopReplay() { try { _player?.Stop(); } catch { } _player?.Dispose(); _stream?.Dispose(); _devices?.Dispose(); _player = null; _stream = null; _devices = null; }
    private void Save()
    {
        var clip = Range(); using var dialog = new SaveFileDialog { Filter = "WAV audio|*.wav", FileName = $"wardogs-{clip.SessionId[..8]}-event-{clip.Marker.Id}.wav", AddExtension = true, DefaultExt = "wav" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        EventMonitor.Save(clip, dialog.FileName, _label.Text, _intent.Text, _notes.Text);
        _info.Text = "Saved audio and a matching JSON label beside it. Keep/Reduce is a review label; no suppression is applied.";
    }
    private void OpenSample()
    {
        if (_running()) throw new InvalidOperationException("Stop live playback before opening an offline sample.");
        using var dialog = new OpenFileDialog { Filter = "Audio/video samples|*.wav;*.mp3;*.mp4|All files|*.*" };
        string samples = Path.Combine(AppContext.BaseDirectory, "Samples");
        if (Directory.Exists(samples)) dialog.InitialDirectory = samples;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        StopReplay(); using var reader = new AudioFileReader(dialog.FileName);
        var monitor = new EventMonitor(reader.WaveFormat, sourceName: Path.GetFileName(dialog.FileName)); monitor.PeakThresholdDb = (double)_threshold.Value;
        var bytes = new byte[reader.WaveFormat.AverageBytesPerSecond / 20]; int count;
        while ((count = reader.Read(bytes, 0, bytes.Length)) > 0) monitor.Tap(bytes, count);
        _offline = monitor; _clip = null; _lastId = -1; RefreshTimeline();
        _info.Text = "Offline sample loaded. Only its latest 60 seconds remain available; click a marker to review.";
    }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Stop(); _timer.Dispose(); StopReplay(); } base.Dispose(disposing); }
}

