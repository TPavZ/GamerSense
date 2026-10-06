using NAudio.Wave;
using System.Buffers.Binary;

namespace GamerSense.Audio;

public sealed record VolumeLevels(double Overall = 100, double Explosions = 100, double Footsteps = 100,
    double GroundVehicles = 100, double AirVehicles = 100, bool Enabled = false)
{
    public double Category(string? category) => category switch
    { "explosions" => Explosions, "footsteps" => Footsteps, "ground_vehicles" => GroundVehicles, "air_vehicles" => AirVehicles, _ => 100 };
}
public sealed class VolumeControls
{
    private VolumeLevels _levels = new();
    public VolumeLevels Levels { get => Volatile.Read(ref _levels); set => Volatile.Write(ref _levels, Sanitize(value)); }
    private static double Clean(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 150) : 100;
    private static VolumeLevels Sanitize(VolumeLevels v) => new(Clean(v.Overall), Clean(v.Explosions), Clean(v.Footsteps), Clean(v.GroundVehicles), Clean(v.AirVehicles), v.Enabled);
}

// Applies one smooth gain to a recognized mixed-audio section, NOT isolated stems.
// Native output frames are processed in place; no added queue or lookahead.
public sealed class LiveCategoryVolumes
{
    private readonly VolumeControls _controls;
    private readonly Func<string?> _category;
    private readonly WaveFormat _format;
    private readonly bool _float, _supported;
    private double _gain = 1, _target = 1, _step;
    private int _remaining;
    private long _clipped;
    public long ClippedSamples => Interlocked.Read(ref _clipped);
    public bool Supported => _supported;
    public LiveCategoryVolumes(WaveFormat format, VolumeControls controls, Func<string?> category)
    {
        _format = format; _controls = controls; _category = category;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            if (ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }
        _float = encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        _supported = _float || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32;
    }
    public void Process(byte[] data, int offset, int count)
    {
        if (!_supported || count == 0) return;
        if (offset < 0 || count < 0 || offset > data.Length - count || count % _format.BlockAlign != 0) throw new ArgumentException("Invalid native audio range.");
        var levels = _controls.Levels;
        double target = levels.Overall / 100 * (levels.Enabled ? levels.Category(_category()) / 100 : 1);
        if (target != _target)
        {
            _target = target; _remaining = Math.Max(1, _format.SampleRate / 50); // 20 ms click-avoidance ramp.
            _step = (_target - _gain) / _remaining;
        }
        if (_gain == 1 && _target == 1) return; // Exact native-byte identity at neutral settings.
        int bytes = _format.BitsPerSample / 8; long clipped = 0;
        for (int frame = offset; frame < offset + count; frame += _format.BlockAlign)
        {
            if (_remaining > 0) { _gain += _step; if (--_remaining == 0) _gain = _target; }
            if (_gain == 1) continue;
            for (int c = 0; c < _format.Channels; c++)
            {
                int i = frame + c * bytes;
                double sample = _float ? BitConverter.ToSingle(data, i) : bytes switch
                { 2 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i,2)) / 32768.0,
                  3 => ((data[i] | data[i+1] << 8 | data[i+2] << 16) << 8 >> 8) / 8388608.0,
                  _ => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i,4)) / 2147483648.0 };
                double output = double.IsFinite(sample) ? sample * _gain : 0;
                if (output < -1 || output > 1) clipped++;
                output = Math.Clamp(output, -1, 1);
                if (_float) BitConverter.TryWriteBytes(data.AsSpan(i,4), (float)output);
                else if (bytes == 2) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i,2), (short)Math.Clamp(Math.Round(output*32768), short.MinValue, short.MaxValue));
                else if (bytes == 3)
                {
                    int v = (int)Math.Clamp(Math.Round(output*8388608), -8388608, 8388607);
                    data[i]=(byte)v; data[i+1]=(byte)(v>>8); data[i+2]=(byte)(v>>16);
                }
                else BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i,4), (int)Math.Clamp(Math.Round(output*2147483648), int.MinValue, int.MaxValue));
            }
        }
        Interlocked.Add(ref _clipped, clipped);
    }
}
