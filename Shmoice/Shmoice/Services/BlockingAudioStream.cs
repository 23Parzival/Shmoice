using System.IO;
using System.Threading;

namespace Shmoice.Services;

/// <summary>
/// A read-only stream fed by a microphone. The speech engine treats a 0-byte read as
/// "end of audio", so <see cref="Read"/> waits for data instead of returning 0 until
/// <see cref="Complete"/> is called.
/// </summary>
internal sealed class BlockingAudioStream : Stream
{
    private readonly Queue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;
    private bool _completed;
    private long _position;

    public void Write(byte[] buffer, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        lock (_chunks)
        {
            if (_completed) return;
            _chunks.Enqueue(copy);
            Monitor.PulseAll(_chunks);
        }
    }

    /// <summary>Lets pending and future reads return 0 so the engine stops.</summary>
    public void Complete()
    {
        lock (_chunks)
        {
            _completed = true;
            Monitor.PulseAll(_chunks);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // The engine treats a short read as end-of-stream, so fill the whole request
        // (waiting for the mic as needed) unless we've been told to stop.
        var total = 0;
        lock (_chunks)
        {
            while (total < count)
            {
                if (_current == null || _offset >= _current.Length)
                {
                    if (_chunks.Count > 0) { _current = _chunks.Dequeue(); _offset = 0; }
                    else if (_completed) break;
                    else Monitor.Wait(_chunks);
                    continue;
                }
                var n = Math.Min(count - total, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + total, n);
                _offset += n;
                total += n;
            }
        }
        Interlocked.Add(ref _position, total);
        return total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    // The speech engine compares Position with Length after every read and stops once they meet,
    // so a live mic stream must report a length it never reaches and the real read position.
    public override long Length => long.MaxValue;
    public override long Position { get => Interlocked.Read(ref _position); set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => Position;
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
