using GamerSense.Audio;
using NAudio.Wave;
namespace GamerSense;
public sealed class ClipWaveform : Control
{
    public EventClip? Clip;
    public double RangeStart, RangeEnd = 5;
    public ClipWaveform() { DoubleBuffered = true; Size = new Size(600, 100); BackColor = Color.FromArgb(12, 12, 16); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Clip is null) return;
        var clip = Clip; int bytes = clip.Format.BitsPerSample / 8;
        bool floating = clip.Format.Encoding == WaveFormatEncoding.IeeeFloat || clip.Format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        int start = (int)(RangeStart * clip.Format.SampleRate), end = Math.Min(clip.Audio.Length / clip.Format.BlockAlign, (int)(RangeEnd * clip.Format.SampleRate));
        using var pen = new Pen(Color.MediumTurquoise); using var line = new Pen(Color.DimGray); using var text = new SolidBrush(Color.Silver);
        float center = (Height - 22) / 2f; e.Graphics.DrawLine(line, 0, center, Width, center);
        for (int x = 0; x < Width && end > start; x++)
        {
            int first = start + (int)((long)(end - start) * x / Width), last = start + (int)((long)(end - start) * (x + 1) / Width);
            float min = 0, max = 0;
            for (int frame = first; frame < Math.Max(first + 1, last) && frame < end; frame++)
                for (int c = 0; c < clip.Format.Channels; c++)
                {
                    int i = frame * clip.Format.BlockAlign + c * bytes;
                    float value = floating ? BitConverter.ToSingle(clip.Audio, i) : bytes switch
                    {
                        2 => BitConverter.ToInt16(clip.Audio, i) / 32768f,
                        3 => ((clip.Audio[i] | clip.Audio[i+1] << 8 | clip.Audio[i+2] << 16) << 8 >> 8) / 8388608f,
                        4 => BitConverter.ToInt32(clip.Audio, i) / 2147483648f,
                        _ => 0
                    };
                    if (float.IsFinite(value)) { min = Math.Min(min, value); max = Math.Max(max, value); }
                }
            e.Graphics.DrawLine(pen, x, center - Math.Clamp(max, -1, 1) * center, x, center - Math.Clamp(min, -1, 1) * center);
        }
        e.Graphics.DrawString($"{RangeStart:F2}s — {RangeEnd:F2}s within clip", Font, text, 0, Height - 21);
    }
}
