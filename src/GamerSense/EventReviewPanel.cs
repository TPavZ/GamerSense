using GamerSense.Audio;
using NAudio.Wave;
using NAudio.CoreAudioApi;
namespace GamerSense;

public sealed class EventReviewPanel : FlowLayoutPanel
{
    private readonly Func<EventMonitor?> _live;
    private readonly Func<bool> _running;
    private readonly Func<string?> _outputId;
    private readonly EventLibrary _library;
    private readonly ListBox _saved = new() { Width = 600, Height = 150, DisplayMember = nameof(SavedEvent.Text) };
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Label _storage = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly Label _saveStatus = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly FlowLayoutPanel _liveTools = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false };
    private SavedEvent? _savedItem;
    private bool _refreshingSaved;
    private long _libraryRevision = -1;
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
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(610, 0), Text = "After the game, Stop playback and select a saved clip. Replay, tag, then approve or delete it." };
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
    public EventReviewPanel(Func<EventMonitor?> live, Func<bool> running, Func<string?> outputId, EventLibrary library, Action<bool>? autoSaveChanged = null)
    {
        _live = live; _running = running; _outputId = outputId; _library = library;
        Dock = DockStyle.Fill; FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true; Padding = new Padding(20);
        _events.BackColor = Color.FromArgb(30, 34, 42); _events.ForeColor = Color.White;
        foreach (var control in new Control[] { _before, _after, _trimStart, _trimEnd, _threshold, _label, _intent, _notes })
            control.ForeColor = Color.Black;
        Controls.Add(new Label { Text = "SAVED EVENT REVIEW", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Spikes are saved for after the game. Unreviewed clips remain Pending.", AutoSize = true });
        var autoSave = new CheckBox { Text = "Automatically save spikes and manual marks", AutoSize = true, Checked = _library.AutoSaveEnabled };
        autoSave.CheckedChanged += (_, _) => { _library.AutoSaveEnabled = autoSave.Checked; autoSaveChanged?.Invoke(autoSave.Checked); };
        Controls.Add(autoSave);
        var savedButtons = Row();
        _filter.Items.AddRange(new object[] { "Pending", "Approved", "All saved clips" }); _filter.SelectedIndex = 0;
        savedButtons.Controls.Add(_filter);
        Button LibraryButton(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); savedButtons.Controls.Add(b); return b; }
        LibraryButton("Refresh saved clips", () => RefreshSaved(true));
        LibraryButton("Open saved folder", () => { Directory.CreateDirectory(_library.DirectoryPath); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_library.DirectoryPath) { UseShellExecute = true }); });
        Controls.Add(savedButtons);
        _saved.BackColor = Color.FromArgb(30, 34, 42); _saved.ForeColor = Color.White; _filter.ForeColor = Color.Black;
        Controls.Add(_saved); Controls.Add(_storage); Controls.Add(_saveStatus);
        var decisions = Row();
        void Decision(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); decisions.Controls.Add(b); }
        Decision("Save tags", () => ReviewSaved(false));
        Decision("Approve clip", () => ReviewSaved(true));
        Decision("Delete clip", DeleteSaved);
        Decision("Save loaded clip for later", () => { _library.SaveNow(Range()); RefreshSaved(true); _info.Text = "Saved as Pending. Select it in the saved list to tag and approve."; });
        var showLive = new CheckBox { Text = "Show live timeline and sample tools", AutoSize = true };
        showLive.CheckedChanged += (_, _) => _liveTools.Visible = showLive.Checked;
        Controls.Add(showLive); Controls.Add(_liveTools);
        _liveTools.Controls.Add(new Label { Text = "LIVE TIMELINE — latest 60 seconds", AutoSize = true });
        _liveTools.Controls.Add(new Label { Text = "Markers highlight activity, not verified sound types. Playback stays unchanged.", AutoSize = true });
        var buttons = Row();
        Button Add(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); buttons.Controls.Add(b); return b; }
        Add("Mark moment (Ctrl+Alt+F8)", () => { if (Monitor is null || !Monitor.Supported) throw new InvalidOperationException("Start monitoring with a supported audio format first."); Monitor.Mark(); RefreshTimeline(); });
        Add("Open audio sample", OpenSample);
        Add("Show live session", () => { _offline = null; _clip = null; _lastId = -1; RefreshTimeline(); });
        _liveTools.Controls.Add(buttons);
        var threshold = Row(); threshold.Controls.Add(new Label { Text = "Loud peak threshold (dBFS)", AutoSize = true }); threshold.Controls.Add(_threshold); _liveTools.Controls.Add(threshold);
        _liveTools.Controls.Add(_timeline); _liveTools.Controls.Add(_events);
        var window = Row(); window.Controls.Add(new Label { Text = "Seconds before / after event", AutoSize = true }); window.Controls.Add(_before); window.Controls.Add(_after);
        var load = new Button { Text = "Load / refresh event clip", AutoSize = true, ForeColor = Color.Black }; load.Click += (_, _) => Guard(LoadClip); window.Controls.Add(load); _liveTools.Controls.Add(window);
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
        var save = new Button { Text = "Export WAV + label", AutoSize = true, ForeColor = Color.Black }; save.Click += (_, _) => Guard(Save); review.Controls.Add(save); Controls.Add(review); Controls.Add(_notes); Controls.Add(decisions); Controls.Add(_info);
        _events.SelectedIndexChanged += (_, _) => { if (!_rebuilding) Guard(LoadClip); };
        _saved.SelectedIndexChanged += (_, _) => { if (!_refreshingSaved) Guard(LoadSaved); };
        _filter.SelectedIndexChanged += (_, _) => Guard(() => RefreshSaved(true));
        _timeline.MarkerSelected += marker => { for (int i = 0; i < _events.Items.Count; i++) if (((MarkerRow)_events.Items[i]).Marker.Id == marker.Id) { _events.SelectedIndex = i; break; } };
        _threshold.ValueChanged += (_, _) => { if (Monitor is not null) Monitor.PeakThresholdDb = (double)_threshold.Value; };
        _trimStart.ValueChanged += (_, _) => UpdateRange(); _trimEnd.ValueChanged += (_, _) => UpdateRange();
        _timer.Tick += (_, _) => { RefreshTimeline(); Guard(() => RefreshSaved()); }; _timer.Start();
        Guard(() => RefreshSaved(true));
        StyleButtons(this);
    }
    private static void StyleButtons(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button button) { button.BackColor = Color.WhiteSmoke; button.ForeColor = Color.Black; }
            StyleButtons(control);
        }
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
        _savedItem = null; _refreshingSaved = true; _saved.ClearSelected(); _refreshingSaved = false;
        StopReplay();
        _clip = null; _waveform.Clip = null; _clipSpectrum.Frame = null; _rangeGeneration++;
        _waveform.Invalidate(); _clipSpectrum.Invalidate();
        _clip = Monitor.Extract(selected.Marker, (double)_before.Value, (double)_after.Value);
        _trimStart.Value = 0; _trimEnd.Value = Math.Min(_trimEnd.Maximum, (decimal)(_clip.EndSeconds - _clip.StartSeconds));
        _info.Text = $"Frozen clip {_clip.StartSeconds:F2}–{_clip.EndSeconds:F2}s. Adjust the range to focus your review. Recent events may need refreshing after post-event audio arrives.";
        UpdateRange();
    }
    private void RefreshSaved(bool force = false)
    {
        if (force || _libraryRevision != _library.Revision)
        {
            long revision = _library.Revision;
            string? selected = (_saved.SelectedItem as SavedEvent)?.Id;
            var items = _library.List(force);
            _refreshingSaved = true; _saved.BeginUpdate(); _saved.Items.Clear();
            foreach (var item in items.Where(x => _filter.SelectedIndex == 2 || x.Approved == (_filter.SelectedIndex == 1))) _saved.Items.Add(item);
            if (selected is not null)
                for (int i = 0; i < _saved.Items.Count; i++) if (((SavedEvent)_saved.Items[i]).Id == selected) _saved.SelectedIndex = i;
            _saved.EndUpdate(); _refreshingSaved = false; _libraryRevision = revision;
            _storage.Text = $"{items.Count(x => !x.Approved)} pending • {items.Count(x => x.Approved)} approved • {items.Sum(x => x.AudioBytes) / (1024.0 * 1024):F1} MB saved. No automatic expiry. 2 GB storage budget.";
        }
        _saveStatus.Text = $"Waiting for disk save: {_library.PendingWrites} • Unsaved/skipped: {_library.Skipped}" +
            (_library.LastError.Length > 0 ? "\n" + _library.LastError : "");
    }
    private void LoadSaved()
    {
        if (_saved.SelectedItem is not SavedEvent item) return;
        StopReplay(); _clip = null; _savedItem = null;
        _clip = _library.Load(item); _savedItem = item;
        _rebuilding = true; _events.ClearSelected(); _rebuilding = false;
        _rangeGeneration++; _label.SelectedItem = item.Label; _intent.SelectedItem = item.Intent; _notes.Text = item.Notes;
        _trimStart.Value = Math.Clamp((decimal)item.RangeStart, _trimStart.Minimum, _trimStart.Maximum);
        _trimEnd.Value = Math.Clamp((decimal)item.RangeEnd, _trimEnd.Minimum, _trimEnd.Maximum);
        _info.Text = $"Saved clip • {item.CreatedUtc.ToLocalTime():g} • {(item.Approved ? "Approved" : "Pending")}. Tag it below, adjust the range, then approve or delete.";
        UpdateRange();
    }
    private void ReviewSaved(bool approve)
    {
        if (_savedItem is null) throw new InvalidOperationException("Select a saved clip first.");
        _ = Range();
        _savedItem = _library.Review(_savedItem, _label.Text, _intent.Text, _notes.Text, (double)_trimStart.Value, (double)_trimEnd.Value, approve || _savedItem.Approved);
        RefreshSaved(true);
        _info.Text = approve ? "Approved and saved. Find it in Approved or All saved clips. Keep/Reduce remains a review label." : "Tags and review range saved.";
    }
    private void DeleteSaved()
    {
        if (_savedItem is null) throw new InvalidOperationException("Select a saved clip first.");
        if (MessageBox.Show(this, "Delete this saved clip and its tags? This cannot be undone.", "Delete saved clip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        StopReplay(); _library.Delete(_savedItem); _savedItem = null; _clip = null; _rangeGeneration++;
        _waveform.Clip = null; _clipSpectrum.Frame = null; _waveform.Invalidate(); _clipSpectrum.Invalidate();
        RefreshSaved(true); _info.Text = "Saved clip deleted.";
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
        EventMonitor.Save(clip, dialog.FileName, _label.Text, _intent.Text, _notes.Text, _savedItem?.Approved ?? true);
        _info.Text = "Exported audio and matching JSON." + (_savedItem is { Approved: false } ? " Pending clip is marked unverified." : "") + " Keep/Reduce is a review label; no suppression is applied.";
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

