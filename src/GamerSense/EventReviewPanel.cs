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
    private readonly ListBox _saved = new() { Width = 600, Height = 180, HorizontalScrollbar = true, DisplayMember = nameof(SavedEvent.Text) };
    private string _exportDirectory;
    private readonly Action<string>? _exportFolderChanged;
    private readonly Label _exportPath = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly Label _storage = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly Label _saveStatus = new() { AutoSize = true, MaximumSize = new Size(610, 0) };
    private readonly FlowLayoutPanel _liveTools = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false };
    private SavedEvent? _savedItem;
    private bool _refreshingSaved;
    private long _libraryRevision = -1;
    private EventMonitor? _offline;
    private EventClip? _clip;
    private WasapiOut? _player;
    private ReviewWaveProvider? _reviewAudio;
    private string? _importPath;
    private bool _syncingRange;
    private bool _loadingReview, _draftDirty;
    private long _lastEdit;
    private readonly Label _suggestionInfo = new() { AutoSize = true, MaximumSize = new Size(610, 0), ForeColor = Color.Goldenrod };
    private int _replayGeneration;
    private readonly CheckBox _repeat = new() { Text = "Repeat selection", AutoSize = true };
    private Button? _deleteButton;
    private MMDeviceEnumerator? _devices;
    private readonly AudioTimeline _timeline = new();
    private readonly ClipWaveform _waveform = new();
    private readonly SpectrumView _clipSpectrum = new() { Width = 600, Height = 140 };
    private int _rangeGeneration;
    private readonly ListBox _events = new() { Width = 600, Height = 120, DisplayMember = nameof(MarkerRow.Text) };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(610, 0), Text = "After the game, Stop playback and select a saved clip. Replay, tag, then approve or delete it." };
    private readonly NumericUpDown _before = new() { Minimum = 0, Maximum = 10, Value = 2, DecimalPlaces = 1, Increment = .5m, Width = 75 };
    private readonly NumericUpDown _after = new() { Minimum = 0, Maximum = 10, Value = 3, DecimalPlaces = 1, Increment = .5m, Width = 75 };
    private readonly NumericUpDown _trimStart = new() { Minimum = 0, Maximum = 20, DecimalPlaces = 3, Increment = .01m, Width = 100 };
    private readonly NumericUpDown _trimEnd = new() { Minimum = 0, Maximum = 20, DecimalPlaces = 3, Increment = .01m, Width = 100 };
    private readonly NumericUpDown _threshold = new() { Minimum = -60, Maximum = 0, Value = -18, Width = 75 };
    private readonly ComboBox _label = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
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
    public EventReviewPanel(Func<EventMonitor?> live, Func<bool> running, Func<string?> outputId, EventLibrary library, Action<bool>? autoSaveChanged = null, string? exportDirectory = null, Action<string>? exportFolderChanged = null)
    {
        _live = live; _running = running; _outputId = outputId; _library = library;
        _exportDirectory = exportDirectory ?? ApprovedClipExporter.DefaultDirectory; _exportFolderChanged = exportFolderChanged;
        Dock = DockStyle.Fill; FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true; Padding = new Padding(20);
        _events.BackColor = Color.FromArgb(30, 34, 42); _events.ForeColor = Color.White;
        foreach (var control in new Control[] { _before, _after, _trimStart, _trimEnd, _threshold, _label, _notes })
            control.ForeColor = Color.Black;
        Controls.Add(new Label { Text = "LIVE SPIKE CAPTURE & REVIEW", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Unreviewed clips expire after one hour. Approve exports and removes the clip.", AutoSize = true });
        var autoSave = new CheckBox { Text = "Capture spikes automatically (plus manual marks)", AutoSize = true, Checked = _library.AutoSaveEnabled };
        autoSave.CheckedChanged += (_, _) => { _library.AutoSaveEnabled = autoSave.Checked; autoSaveChanged?.Invoke(autoSave.Checked); };
        Controls.Add(autoSave);
        Controls.Add(new Label { Text = "Spikes and sudden rises are flagged, categorized tentatively, and stored for your review.", AutoSize = true, MaximumSize = new Size(610, 0) });
        var sensitivity = Row(); sensitivity.Controls.Add(new Label { Text = "Loud spike threshold (dBFS)", AutoSize = true }); sensitivity.Controls.Add(_threshold);
        Controls.Add(sensitivity);
        var savedButtons = Row();
        Button LibraryButton(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); savedButtons.Controls.Add(b); return b; }
        LibraryButton("Refresh saved clips", () => RefreshSaved(true));
        LibraryButton("Delete all pending clips", DeleteAllPending);
        LibraryButton("Open WAV for review", OpenReviewWav);
        LibraryButton("Open temporary clips", () => { Directory.CreateDirectory(_library.DirectoryPath); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_library.DirectoryPath) { UseShellExecute = true }); });
        LibraryButton("Choose export folder", ChooseExportFolder);
        LibraryButton("Open approved folder", () => { Directory.CreateDirectory(_exportDirectory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_exportDirectory) { UseShellExecute = true }); });
        Controls.Add(savedButtons); _exportPath.Text = "Approved exports: " + _exportDirectory; Controls.Add(_exportPath);
        _saved.BackColor = Color.FromArgb(30, 34, 42); _saved.ForeColor = Color.White;
        Controls.Add(_saved); Controls.Add(_storage); Controls.Add(_saveStatus); Controls.Add(_suggestionInfo);
        var decisions = Row();
        Button Decision(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); decisions.Controls.Add(b); return b; }
        Decision("Approve & export", ApproveSaved);
        _deleteButton = Decision("Delete clip", DeleteSaved);
        var showLive = new CheckBox { Text = "Show live timeline and sample tools", AutoSize = true };
        showLive.CheckedChanged += (_, _) => _liveTools.Visible = showLive.Checked;
        Controls.Add(showLive); Controls.Add(_liveTools);
        _liveTools.Controls.Add(new Label { Text = "LIVE TIMELINE — latest 60 seconds", AutoSize = true });
        _liveTools.Controls.Add(new Label { Text = "Markers highlight activity, not verified sound types. Playback stays unchanged.", AutoSize = true });
        var buttons = Row();
        Button Add(string text, Action action) { var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Black }; b.Click += (_, _) => Guard(action); buttons.Controls.Add(b); return b; }
        Add("Mark moment (Ctrl+Alt+F8)", () => { if (Monitor is null || !Monitor.Supported) throw new InvalidOperationException("Start monitoring with a supported audio format first."); Monitor.Mark(); RefreshTimeline(); });
        Add("Open audio sample", OpenSample);
        Add("Show live session", () => { FlushDraft(); StopReplay(); _offline = null; _clip = null; _savedItem = null; _importPath = null; _suggestionInfo.Text = ""; _lastId = -1; _rangeGeneration++;
            _waveform.Clip = null; _clipSpectrum.Frame = null; _waveform.Invalidate(); _clipSpectrum.Invalidate();
            if (_deleteButton is not null) _deleteButton.Text = "Delete clip"; RefreshTimeline(); });
        _liveTools.Controls.Add(buttons);
        _liveTools.Controls.Add(_timeline); _liveTools.Controls.Add(_events);
        var window = Row(); window.Controls.Add(new Label { Text = "Seconds before / after event", AutoSize = true }); window.Controls.Add(_before); window.Controls.Add(_after);
        var load = new Button { Text = "Load / refresh event clip", AutoSize = true, ForeColor = Color.Black }; load.Click += (_, _) => Guard(LoadClip); window.Controls.Add(load); _liveTools.Controls.Add(window);
        var trim = Row(); trim.Controls.Add(new Label { Text = "Start / end within clip (seconds)", AutoSize = true }); trim.Controls.Add(_trimStart); trim.Controls.Add(_trimEnd);
        var replay = new Button { Text = "Replay range", AutoSize = true, ForeColor = Color.Black }; replay.Click += (_, _) => Guard(Replay); trim.Controls.Add(replay);
        var stop = new Button { Text = "Stop replay", AutoSize = true, ForeColor = Color.Black }; stop.Click += (_, _) => { StopReplay(); _info.Text = "Replay stopped."; }; trim.Controls.Add(stop);
        trim.Controls.Add(_repeat);
        var whole = new Button { Text = "Whole clip", AutoSize = true, ForeColor = Color.Black };
        whole.Click += (_, _) => { if (_clip is not null) SetRange(0, _clip.Audio.Length / (double)_clip.Format.AverageBytesPerSecond); };
        trim.Controls.Add(whole);
        var focus = new Button { Text = "Focus spike", AutoSize = true, ForeColor = Color.Black };
        focus.Click += (_, _) => { if (_clip is null) return;
            var suggestion = _savedItem?.AutoSuggestion;
            double duration = _clip.Audio.Length / (double)_clip.Format.AverageBytesPerSecond;
            double start = suggestion is { WindowsAnalyzed: > 0 } ? suggestion.AnalysisStartClipSeconds : Math.Max(0, _clip.Marker.Seconds - _clip.StartSeconds - .2);
            double end = suggestion is { WindowsAnalyzed: > 0 } ? suggestion.AnalysisEndClipSeconds : Math.Min(duration, start + .7);
            if (start < end) SetRange(start, end);
        };
        trim.Controls.Add(focus); Controls.Add(trim);
        Controls.Add(_waveform);
        Controls.Add(new Label { Text = "Drag across the waveform to select a sound; drag gold boundaries to refine it. Approve exports only the selection.", AutoSize = true });
        Controls.Add(_clipSpectrum);
        var review = Row();
        _label.Items.AddRange(new object[] { "movement", "nearby footsteps", "own footsteps", "gunfire", "reload", "explosions / mortars", "air vehicle", "ground vehicle", "building / repair", "horn / alarm", "speech", "ambience", "mixed / uncertain", "grenade handling / throw", "detonator activation", "chambering", "footsteps" }); _label.SelectedIndex = 12;
        review.Controls.Add(_label);
        Controls.Add(review); Controls.Add(_notes); Controls.Add(decisions); Controls.Add(_info);
        _events.SelectedIndexChanged += (_, _) => { if (!_rebuilding) Guard(LoadClip); };
        _saved.SelectedIndexChanged += (_, _) => { if (!_refreshingSaved) Guard(LoadSaved); };
        _timeline.MarkerSelected += marker => { for (int i = 0; i < _events.Items.Count; i++) if (((MarkerRow)_events.Items[i]).Marker.Id == marker.Id) { _events.SelectedIndex = i; break; } };
        _threshold.ValueChanged += (_, _) => { if (Monitor is not null) Monitor.PeakThresholdDb = (double)_threshold.Value; };
        _trimStart.ValueChanged += (_, _) => { if (!_syncingRange) { UpdateRange(); MarkEdited(); } }; _trimEnd.ValueChanged += (_, _) => { if (!_syncingRange) { UpdateRange(); MarkEdited(); } };
        _label.SelectedIndexChanged += (_, _) => MarkEdited(); _notes.TextChanged += (_, _) => MarkEdited();
        _waveform.RangeSelected += (start, end) => SetRange(start, end);
        _repeat.CheckedChanged += (_, _) => { if (_reviewAudio is not null) _reviewAudio.Repeat = _repeat.Checked; };
        _timer.Tick += (_, _) => { RefreshTimeline(); if (_draftDirty && Environment.TickCount64 - _lastEdit >= 600) Guard(FlushDraft); Guard(() => RefreshSaved());
            _waveform.Playhead = _reviewAudio is null ? null : (double)_trimStart.Value + _reviewAudio.PositionSeconds; _waveform.Invalidate(); }; _timer.Start();
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
        FlushDraft(); _draftDirty = false; _suggestionInfo.Text = "Live timeline selection; choose a stored capture to approve.";
        _savedItem = null; _importPath = null; if (_deleteButton is not null) _deleteButton.Text = "Delete clip"; _refreshingSaved = true; _saved.ClearSelected(); _refreshingSaved = false;
        StopReplay();
        _clip = null; _waveform.Clip = null; _clipSpectrum.Frame = null; _rangeGeneration++;
        _waveform.Invalidate(); _clipSpectrum.Invalidate();
        _clip = Monitor.Extract(selected.Marker, (double)_before.Value, (double)_after.Value);
        _label.SelectedIndex = 12; _notes.Clear(); SetRange(0, _clip.Audio.Length / (double)_clip.Format.AverageBytesPerSecond);
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
            foreach (var item in items) _saved.Items.Add(item);
            if (selected is not null)
                for (int i = 0; i < _saved.Items.Count; i++) if (((SavedEvent)_saved.Items[i]).Id == selected) _saved.SelectedIndex = i;
            _saved.EndUpdate(); _refreshingSaved = false; _libraryRevision = revision;
            _storage.Text = $"{items.Length} awaiting export/review • {items.Sum(x => x.AudioBytes) / (1024.0 * 1024):F1} MB saved. One-hour expiry • 2 GB limit.";
            if (_savedItem is not null && !items.Any(x => x.Id == _savedItem.Id))
            {
                StopReplay(); _savedItem = null; _clip = null; _draftDirty = false; _suggestionInfo.Text = ""; _rangeGeneration++;
                _waveform.Clip = null; _clipSpectrum.Frame = null; _waveform.Invalidate(); _clipSpectrum.Invalidate();
                _info.Text = "The selected saved clip expired or was deleted. Select another clip.";
            }
        }
        _saveStatus.Text = $"Waiting for disk save: {_library.PendingWrites} • Unsaved/skipped: {_library.Skipped}" +
            (_library.LastError.Length > 0 ? "\n" + _library.LastError : "");
    }
    private void LoadSaved()
    {
        FlushDraft();
        if (_saved.SelectedItem is not SavedEvent item) return;
        item = _library.List().FirstOrDefault(x => x.Id == item.Id) ?? item;
        _loadingReview = true;
        try
        {
        StopReplay(); _clip = null; _savedItem = null;
        _clip = _library.Load(item); _savedItem = item; _importPath = null; if (_deleteButton is not null) _deleteButton.Text = "Delete clip";
        _rebuilding = true; _events.ClearSelected(); _rebuilding = false;
        _rangeGeneration++;
        string initialLabel = item.HasManualEdits || item.Approved ? item.Label : item.AutoSuggestion?.SuggestedLabel ?? item.Label;
        if (!_label.Items.Contains(initialLabel)) _label.Items.Add(initialLabel);
        _label.SelectedItem = initialLabel; _notes.Text = item.Notes;
        var suggestion = item.AutoSuggestion;
        _suggestionInfo.Text = suggestion is null ? "Older capture: no automatic suggestion stored." :
            suggestion.Text + "\n" + suggestion.Reason +
            (suggestion.Alternatives.Length > 1 ? "\nAlternatives: " + string.Join(", ", suggestion.Alternatives.Skip(1).Select(c => c.Label)) : "") +
            (suggestion.WindowsAnalyzed > 0 ? $"\nAnalyzed {suggestion.AnalysisStartClipSeconds:F2}–{suggestion.AnalysisEndClipSeconds:F2}s within this clip. Suggestions are unverified." : "");
        SetRange(item.RangeStart, item.RangeEnd);
        _info.Text = $"Captured clip • {item.CreatedUtc.ToLocalTime():g}. Replay, correct the proposed label, then approve or delete. Valid edits save automatically while pending.";
        UpdateRange();
        }
        finally { _loadingReview = false; _draftDirty = false; }
    }
    private void MarkEdited()
    {
        if (_loadingReview || _savedItem is null || _clip is null) return;
        _draftDirty = true; _lastEdit = Environment.TickCount64;
    }
    private void FlushDraft()
    {
        if (!_draftDirty || _savedItem is null) return;
        _ = Range();
        _savedItem = _library.Review(_savedItem, _label.Text, "Unspecified", _notes.Text, (double)_trimStart.Value, (double)_trimEnd.Value, false);
        _draftDirty = false;
    }
    public void FlushPendingEdits() => Guard(FlushDraft);
    private void ChooseExportFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder for approved WAV + JSON labels", UseDescriptionForTitle = true, SelectedPath = _exportDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _exportDirectory = dialog.SelectedPath; _exportFolderChanged?.Invoke(_exportDirectory);
        _exportPath.Text = "Approved exports: " + _exportDirectory;
    }
    private void ApproveSaved()
    {
        if (_savedItem is null && _importPath is null) throw new InvalidOperationException("Select a saved clip or open a WAV first.");
        FlushDraft(); StopReplay();
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_exportDirectory));
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_library.DirectoryPath));
        if (target.Equals(temporary, StringComparison.OrdinalIgnoreCase) || target.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an approved export folder outside the temporary clip library.");
        string path = _savedItem is not null
            ? ApprovedClipExporter.Approve(_library, _savedItem, Range(), _exportDirectory, _label.Text, _notes.Text)
            : ApprovedClipExporter.ExportImported(_clip!, Range(), _importPath!, _exportDirectory, _label.Text, _notes.Text);
        _importPath = null; if (_deleteButton is not null) _deleteButton.Text = "Delete clip";
        _savedItem = null; _clip = null; _draftDirty = false; _suggestionInfo.Text = ""; _rangeGeneration++;
        _waveform.Clip = null; _clipSpectrum.Frame = null; _waveform.Invalidate(); _clipSpectrum.Invalidate();
        RefreshSaved(true); _info.Text = "Approved sample exported with its label, then removed from review: " + path;
        if (_saved.Items.Count > 0) _saved.SelectedIndex = 0;
    }
    private void DeleteSaved()
    {
        if (_importPath is not null)
        {
            StopReplay(); _importPath = null; _clip = null; _rangeGeneration++; _waveform.Clip = null; _waveform.Invalidate();
            _clipSpectrum.Frame = null; _clipSpectrum.Invalidate(); if (_deleteButton is not null) _deleteButton.Text = "Delete clip";
            _info.Text = "Sample closed. The original WAV and label were kept."; return;
        }
        if (_savedItem is null) throw new InvalidOperationException("Select a saved clip first.");
        if (MessageBox.Show(this, "Delete this saved clip and its tags? This cannot be undone.", "Delete saved clip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        StopReplay(); _library.Delete(_savedItem); _savedItem = null; _clip = null; _draftDirty = false; _suggestionInfo.Text = ""; _rangeGeneration++;
        _waveform.Clip = null; _clipSpectrum.Frame = null; _waveform.Invalidate(); _clipSpectrum.Invalidate();
        RefreshSaved(true); _info.Text = "Saved clip deleted.";
    }
    private void DeleteAllPending()
    {
        var pending = _library.List(true).Where(item => !item.Approved).ToArray();
        if (pending.Length == 0) { _info.Text = "There are no pending clips to delete."; return; }
        string message = $"Delete all {pending.Length} pending clips and their labels?\n\nThis includes edited clips awaiting approval. This cannot be undone.\nApproved exports and imported WAV files will be kept.";
        if (_running()) message += "\n\nLive capture is still running; new clips can appear after deletion.";
        if (MessageBox.Show(this, message, "Delete all pending clips", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        bool selected = _savedItem is not null && pending.Any(item => item.Id == _savedItem.Id);
        if (selected) StopReplay();
        int removed;
        try
        {
            removed = _library.DeletePending(pending);
        }
        finally
        {
            RefreshSaved(true);
        }
        _info.Text = $"Deleted {removed} pending clips. Approved exports were kept.";
    }
    private async void UpdateRange()
    {
        StopReplay(); int generation = ++_rangeGeneration;
        if (_clip is null) return;
        _clipSpectrum.Frame = null; _clipSpectrum.Invalidate();
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
        return ReviewRange.Select(_clip, (double)_trimStart.Value, (double)_trimEnd.Value);
    }
    private void SetRange(double start, double end)
    {
        if (_clip is null) return;
        _syncingRange = true;
        try
        {
            decimal duration = (decimal)(_clip.Audio.Length / (double)_clip.Format.AverageBytesPerSecond);
            _trimStart.Value = 0; _trimEnd.Value = 0;
            _trimStart.Maximum = duration; _trimEnd.Maximum = duration;
            _trimStart.Value = Math.Clamp((decimal)start, 0, duration); _trimEnd.Value = Math.Clamp((decimal)end, 0, duration);
        }
        finally { _syncingRange = false; }
        UpdateRange(); MarkEdited();
    }
    private void OpenReviewWav()
    {
        if (_running()) throw new InvalidOperationException("Stop live playback before reviewing an existing clip.");
        using var dialog = new OpenFileDialog { Filter = "WAV clips|*.wav", InitialDirectory = _exportDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        FlushDraft(); var sample = ReviewRange.OpenWav(dialog.FileName); _draftDirty = false; _suggestionInfo.Text = "Imported sample: review its existing label and selection.";
        StopReplay(); _refreshingSaved = true; _saved.ClearSelected(); _refreshingSaved = false;
        _savedItem = null; _clip = sample.Clip; _importPath = dialog.FileName;
        _rebuilding = true; _events.ClearSelected(); _rebuilding = false;
        if (!_label.Items.Contains(sample.Label)) _label.Items.Add(sample.Label);
        _label.SelectedItem = sample.Label; _notes.Text = sample.Notes;
        if (_deleteButton is not null) _deleteButton.Text = "Close sample";
        SetRange(0, _clip.Audio.Length / (double)_clip.Format.AverageBytesPerSecond);
        _info.Text = "Existing sample loaded. Select the exact sound and approve to export a new WAV + label. The original stays intact.";
    }

    private void Replay()
    {
        if (_running()) throw new InvalidOperationException("Stop live playback before replaying a clip.");
        var clip = Range(); string output = _outputId() ?? throw new InvalidOperationException("Select your true output on the Playback tab first.");
        StopReplay();
        _devices = new MMDeviceEnumerator(); _player = new WasapiOut(_devices.GetDevice(output), AudioClientShareMode.Shared, true, 30);
        _reviewAudio = new ReviewWaveProvider(clip, _repeat.Checked); _player.Init(_reviewAudio);
        int generation = _replayGeneration;
        _player.PlaybackStopped += (_, args) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (generation != _replayGeneration || IsDisposed) return;
                StopReplay(); _info.Text = args.Exception is null ? "Replay finished." : "Replay stopped: " + args.Exception.Message; })); }
            catch (InvalidOperationException) { }
        };
        _player.Play();
        _info.Text = _repeat.Checked ? "Repeating the selection. Stop replay to finish." : "Replaying the selection through your true output.";
    }
    public void StopReplay()
    {
        _replayGeneration++; try { _player?.Stop(); } catch { }
        _player?.Dispose(); _devices?.Dispose(); _player = null; _reviewAudio = null; _devices = null;
        _waveform.Playhead = null; _waveform.Invalidate();
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
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Stop(); _timer.Dispose(); Guard(FlushDraft); StopReplay(); } base.Dispose(disposing); }
}

