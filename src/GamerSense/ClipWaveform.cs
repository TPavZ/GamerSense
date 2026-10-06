using GamerSense.Audio;
using NAudio.Wave;
namespace GamerSense;

public sealed class ClipWaveform : Control
{
    public EventClip? Clip;
    public double RangeStart, RangeEnd = 5;
    public double? Playhead;
    public event Action<double, double>? RangeSelected;
    private EventClip? _cached;
    private float[] _mins = [], _maxs = [];
    private double _anchor, _dragStart, _dragEnd;
    private int _dragMode;
    private double Duration => Clip is null ? 0 : Clip.Audio.Length / (double)Clip.Format.AverageBytesPerSecond;
    public ClipWaveform()
    {
        DoubleBuffered = true; Size = new Size(600, 160); BackColor = Color.FromArgb(12, 12, 16);
        Cursor = Cursors.Cross; AccessibleName = "Clip waveform: drag a range or its boundaries";
    }
    private float X(double seconds) => (float)(Math.Clamp(seconds / Math.Max(Duration, .000001), 0, 1) * (Width - 1));
    private double Seconds(int x) => Math.Clamp(x / (double)Math.Max(1, Width - 1), 0, 1) * Duration;
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left || Clip is null) return;
        _anchor = Seconds(e.X); _dragStart = RangeStart; _dragEnd = RangeEnd;
        _dragMode = Math.Abs(e.X - X(RangeStart)) <= 8 ? 1 : Math.Abs(e.X - X(RangeEnd)) <= 8 ? 2 : 3;
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (!Capture || _dragMode == 0 || Clip is null) return;
        double current = Seconds(e.X), minimum = Math.Min(Duration, Math.Max(.001, Duration / Math.Max(1, Width - 1)));
        if (_dragMode == 1) RangeStart = Math.Min(current, Math.Max(0, _dragEnd - minimum));
        else if (_dragMode == 2) RangeEnd = Math.Max(current, Math.Min(Duration, _dragStart + minimum));
        else { RangeStart = Math.Min(_anchor, current); RangeEnd = Math.Max(_anchor, current); }
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (_dragMode == 0) return;
        _dragMode = 0; Capture = false;
        if (RangeEnd > RangeStart) RangeSelected?.Invoke(RangeStart, RangeEnd);
        else { RangeStart = _dragStart; RangeEnd = _dragEnd; Invalidate(); }
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _dragMode != 0) { _dragMode = 0; RangeStart = _dragStart; RangeEnd = _dragEnd; Invalidate(); }
    }
    private void CacheWaveform(EventClip clip)
    {
        if (_cached == clip && _mins.Length == Width) return;
        _cached = clip; _mins = new float[Width]; _maxs = new float[Width];
        int bytes = clip.Format.BitsPerSample / 8, frames = clip.Audio.Length / clip.Format.BlockAlign;
        bool floating = clip.Format.Encoding == WaveFormatEncoding.IeeeFloat || clip.Format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        for (int x = 0; x < Width; x++)
        {
            int first = (int)((long)frames * x / Width), last = Math.Min(frames, Math.Max(first + 1, (int)((long)frames * (x + 1) / Width)));
            for (int frame = first; frame < last; frame++)
                for (int c = 0; c < clip.Format.Channels; c++)
                {
                    int i = frame * clip.Format.BlockAlign + c * bytes;
                    float value = floating && bytes == 4 ? BitConverter.ToSingle(clip.Audio, i) : bytes switch
                    {
                        1 => (clip.Audio[i] - 128) / 128f,
                        2 => BitConverter.ToInt16(clip.Audio, i) / 32768f,
                        3 => ((clip.Audio[i] | clip.Audio[i+1] << 8 | clip.Audio[i+2] << 16) << 8 >> 8) / 8388608f,
                        4 => BitConverter.ToInt32(clip.Audio, i) / 2147483648f,
                        _ => 0
                    };
                    if (float.IsFinite(value)) { _mins[x] = Math.Min(_mins[x], value); _maxs[x] = Math.Max(_maxs[x], value); }
                }
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var text = new SolidBrush(Color.Silver);
        if (Clip is null) { e.Graphics.DrawString("Select a saved clip or open a WAV to review.", Font, text, 10, 15); return; }
        CacheWaveform(Clip);
        using var pen = new Pen(Color.MediumTurquoise); using var line = new Pen(Color.DimGray);
        using var shade = new SolidBrush(Color.FromArgb(45, Color.MediumTurquoise)); using var boundary = new Pen(Color.Goldenrod, 2);
        float left = X(RangeStart), right = X(RangeEnd), center = (Height - 28) / 2f;
        e.Graphics.FillRectangle(shade, left, 0, Math.Max(0, right - left), Height - 27);
        e.Graphics.DrawLine(line, 0, center, Width, center);
        for (int x = 0; x < Width; x++)
            e.Graphics.DrawLine(pen, x, center - Math.Clamp(_maxs[x], -1, 1) * center, x, center - Math.Clamp(_mins[x], -1, 1) * center);
        e.Graphics.DrawLine(boundary, left, 0, left, Height - 27); e.Graphics.DrawLine(boundary, right, 0, right, Height - 27);
        double marker = Clip.Marker.Seconds - Clip.StartSeconds;
        if (marker >= 0 && marker <= Duration) { using var eventPen = new Pen(Color.Silver) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }; e.Graphics.DrawLine(eventPen, X(marker), 0, X(marker), Height - 27); }
        if (Playhead is double position) { using var head = new Pen(Color.White, 2); e.Graphics.DrawLine(head, X(position), 0, X(position), Height - 27); }
        e.Graphics.DrawString($"0s    Selected {RangeStart:F3}–{RangeEnd:F3}s ({Math.Max(0, RangeEnd - RangeStart):F3}s)    Clip {Duration:F3}s", Font, text, 0, Height - 24);
    }
}
