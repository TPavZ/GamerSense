using GamerSense.Audio;
namespace GamerSense;

public sealed class SpectrumView : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AnalysisFrame? Frame { get; set; }
    public SpectrumView()
    {
        DoubleBuffered = true;
        Size = new Size(430, 190);
        BackColor = Color.FromArgb(12, 12, 16);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var frame = Frame;
        if (frame is null) return;
        float top = 12, bottom = Height - 25, left = 35, right = Width - 10;
        double maxHz = Math.Min(20000, frame.SampleRate / 2.0);
        using var grid = new Pen(Color.FromArgb(55, 55, 65));
        using var brush = new SolidBrush(Color.Silver);
        using var bars = new SolidBrush(Color.FromArgb(60, 205, 180));
        for (int db = 0; db >= -80; db -= 20)
        {
            float y = top + -db / 100f * (bottom - top);
            e.Graphics.DrawLine(grid, left, y, right, y);
            e.Graphics.DrawString(db.ToString(), Font, brush, 0, y - 7);
        }
        for (int b = 0; b < 64; b++)
        {
            double low = 20 * Math.Pow(maxHz / 20, b / 64.0);
            double high = 20 * Math.Pow(maxHz / 20, (b + 1) / 64.0);
            int first = Math.Max(1, (int)Math.Ceiling(low * LiveAnalyzer.FftSize / frame.SampleRate));
            int last = Math.Min(frame.BinsDb.Length - 1, (int)Math.Ceiling(high * LiveAnalyzer.FftSize / frame.SampleRate));
            float db = -100;
            for (int i = first; i <= last; i++) db = Math.Max(db, frame.BinsDb[i]);
            float h = Math.Clamp((db + 100) / 100, 0, 1) * (bottom - top);
            e.Graphics.FillRectangle(bars, left + b * (right - left) / 64, bottom - h, Math.Max(1, (right - left) / 64 - 1), h);
        }
        foreach (int hz in new[] { 20, 100, 1000, 10000 })
        {
            if (hz > maxHz) continue;
            float x = left + (float)(Math.Log(hz / 20.0) / Math.Log(maxHz / 20)) * (right - left);
            e.Graphics.DrawString(hz >= 1000 ? (hz / 1000) + "k" : hz.ToString(), Font, brush, x, bottom + 3);
        }
    }
}
