using System.Buffers;
using System.Threading.Channels;

namespace GamerSense.Audio;

// Only this pooled byte copy runs on the audio callback. Decoding is lower-priority work.
public sealed class AnalysisTap : IDisposable
{
    private readonly record struct Packet(byte[] Buffer, int Count, long Sequence, long CapturedAt);
    private readonly Channel<Packet> _queue;
    private readonly Action<byte[], int> _consume;
    private readonly Action _reset;
    private readonly Thread _worker;
    private long _sequence, _dropped;
    private int _disposed;
    public long DroppedPackets => Interlocked.Read(ref _dropped);

    public AnalysisTap(Action<byte[], int> consume, Action reset, int capacity = 3)
    {
        _consume = consume; _reset = reset;
        _queue = Channel.CreateBounded<Packet>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _worker = new Thread(Run) { IsBackground = true, Name = "GamerSense analysis tap", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }
    public void Enqueue(byte[] buffer, int count)
    {
        if (Volatile.Read(ref _disposed) != 0 || count <= 0) return;
        long sequence = Interlocked.Increment(ref _sequence);
        if (count > buffer.Length || count > 1024 * 1024) { Interlocked.Increment(ref _dropped); return; }
        byte[] copy = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        if (!_queue.Writer.TryWrite(new Packet(copy, count, sequence, Environment.TickCount64)))
        {
            ArrayPool<byte>.Shared.Return(copy);
            Interlocked.Increment(ref _dropped);
        }
    }
    private void Run()
    {
        long previous = 0;
        try
        {
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (_queue.Reader.TryRead(out var packet))
                {
                    try
                    {
                        if (Volatile.Read(ref _disposed) != 0 || Environment.TickCount64 - packet.CapturedAt > 100)
                        {
                            Interlocked.Increment(ref _dropped); continue;
                        }
                        if (previous != 0 && packet.Sequence != previous + 1) _reset();
                        previous = packet.Sequence;
                        _consume(packet.Buffer, packet.Count);
                    }
                    catch { previous = 0; try { _reset(); } catch { } }
                    finally { ArrayPool<byte>.Shared.Return(packet.Buffer); }
                }
        }
        finally
        {
            while (_queue.Reader.TryRead(out var remaining)) ArrayPool<byte>.Shared.Return(remaining.Buffer);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        if (Thread.CurrentThread != _worker) _worker.Join(500);
    }
}
