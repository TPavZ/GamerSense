using GamerSense.Audio;
namespace GamerSense;

public sealed class AudioTimeline : Control
{
    public LevelPoint[] Points = Array.Empty<LevelPoint>();
    public AudioMarker[] Markers = Array.Empty<AudioMarker>();
    public double StartSeconds;
    public double EndSeconds = 60;
    public event Action<AudioMarker>? MarkerSelected;
    public AudioTimeline() { DoubleBuffered = true; Size = new Size(600, 150); BackColor = Color.FromArgb(12, 12, 16); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        double span = Math.Max(.001, EndSeconds - StartSeconds);
        float X(double seconds) => (float)((seconds - StartSeconds) / span * (Width - 1));
        using var level = new Pen(Color.MediumTurquoise, 2);
        using var grid = new Pen(Color.FromArgb(50, 50, 60));
        using var text = new SolidBrush(Color.Silver);
        for (int db = -20; db >= -80; db -= 20)
        {
            float y = (Height - 22) * -db / 100f;
            e.Graphics.DrawLine(grid, 0, y, Width, y);
        }
        var points = Points.Where(p => p.Seconds >= StartSeconds && p.Seconds <= EndSeconds).ToArray();
        for (int i = 1; i < points.Length; i++)
            e.Graphics.DrawLine(level, X(points[i-1].Seconds), (Height - 22) * Math.Clamp(-points[i-1].PeakDb / 100, 0, 1),
                X(points[i].Seconds), (Height - 22) * Math.Clamp(-points[i].PeakDb / 100, 0, 1));
        foreach (var marker in Markers.Where(x => x.Seconds >= StartSeconds && x.Seconds <= EndSeconds))
        {
            using var pen = new Pen(marker.Kind == "Manual mark" ? Color.Gold : Color.Tomato, 2);
            e.Graphics.DrawLine(pen, X(marker.Seconds), 0, X(marker.Seconds), Height - 22);
        }
        e.Graphics.DrawString($"{StartSeconds:F1}s", Font, text, 0, Height - 21);
        e.Graphics.DrawString($"{EndSeconds:F1}s", Font, text, Width - 75, Height - 21);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        double seconds = StartSeconds + e.X / (double)Math.Max(1, Width - 1) * (EndSeconds - StartSeconds);
        var marker = Markers.Where(x => x.Seconds >= StartSeconds && x.Seconds <= EndSeconds).OrderBy(x => Math.Abs(x.Seconds - seconds)).FirstOrDefault();
        if (marker is not null) MarkerSelected?.Invoke(marker);
    }
}

