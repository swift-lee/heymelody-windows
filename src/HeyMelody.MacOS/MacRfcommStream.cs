using System.Threading.Channels;

namespace HeyMelody.MacOS;

internal sealed class MacRfcommStream(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write, Action close) : Stream
{
    private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _readGate = new(1), _writeGate = new(1);
    private byte[]? _current;
    private int _offset, _buffered, _disposed, _ended;
    private Exception? _failure;
    internal void Receive(byte[] data)
    {
        if (_disposed != 0 || data.Length == 0) return;
        if (Interlocked.Add(ref _buffered, data.Length) > 1024 * 1024)
        { Complete(new IOException("RFCOMM receive buffer exceeded its limit.")); close(); return; }
        if (!_received.Writer.TryWrite(data)) Interlocked.Add(ref _buffered, -data.Length);
    }
    internal void Complete(Exception? error)
    { _failure = error; Interlocked.Exchange(ref _ended, 1); _received.Writer.TryComplete(error); }
    public override bool CanRead => _disposed == 0;
    public override bool CanWrite => _disposed == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.Length == 0) return 0;
        await _readGate.WaitAsync(token);
        try
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _received.Reader.WaitToReadAsync(token)) return 0;
                if (!_received.Reader.TryRead(out _current)) continue;
                _offset = 0;
                Interlocked.Add(ref _buffered, -_current.Length);
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return count;
        }
        finally { _readGate.Release(); }
    }
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.Length == 0) return;
        await _writeGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_ended != 0) throw new IOException("RFCOMM channel is closed.", _failure);
            await write(buffer, token);
        }
        finally { _writeGate.Release(); }
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        { Complete(new ObjectDisposedException(nameof(MacRfcommStream))); close(); }
        base.Dispose(disposing);
    }
}
