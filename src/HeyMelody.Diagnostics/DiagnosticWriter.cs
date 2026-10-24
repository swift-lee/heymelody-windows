using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HeyMelody.Diagnostics;

internal sealed class DiagnosticWriter : IDisposable
{
    private readonly DiagnosticSessionInfo _session;
    private readonly string _component;
    private readonly string _role;
    private readonly string _instance;
    private readonly Action<string, Exception?> _failure;
    private readonly long _rotationBytes;
    private readonly Channel<DiagnosticEntry> _queue;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly Task _pump;
    private Stream _file;
    private readonly Func<string, Stream>? _streamFactory;
    private long _length, _sequence, _dropped, _pending;
    private int _part, _enabled = 1, _disposed, _overflowReported, _timeoutReported;

    public DiagnosticWriter(DiagnosticSessionInfo session, string component, string role, string instance,
        Action<string, Exception?> failure, long rotationBytes, int capacity, Func<string, Stream>? streamFactory = null)
    {
        _session = session; _component = component; _role = role; _instance = instance;
        _failure = failure; _rotationBytes = rotationBytes; _streamFactory = streamFactory;
        _queue = Channel.CreateBounded<DiagnosticEntry>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        Directory.CreateDirectory(session.Directory!);
        _file = OpenPart();
        _pump = Task.Run(PumpAsync);
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0 && Volatile.Read(ref _disposed) == 0;

    public void Enqueue(DiagnosticEntry entry)
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _pending);
        _idle.Reset();
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Decrement(ref _pending);
            if (Volatile.Read(ref _disposed) != 0) return;
            Interlocked.Increment(ref _dropped);
            if (Interlocked.Exchange(ref _overflowReported, 1) == 0)
                _failure("日志队列已满，本组件日志出现缺口；后续可写入的事件仍将记录。", null);
        }
        Wake();
    }

    private void Wake()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                var lost = Interlocked.Exchange(ref _dropped, 0);
                if (lost != 0)
                    await WriteEntryAsync(new(DateTimeOffset.UtcNow, DiagnosticLevel.Warning, "Diagnostics", "logging.dropped",
                        "Diagnostic queue overflow", null, Environment.CurrentManagedThreadId,
                        JsonSerializer.SerializeToElement(new { lostEvents = lost }), null)).ConfigureAwait(false);
                while (_queue.Reader.TryRead(out var entry))
                {
                    await WriteEntryAsync(entry).ConfigureAwait(false);
                    Interlocked.Decrement(ref _pending);
                }
                await _file.FlushAsync().ConfigureAwait(false);
                if (Interlocked.Read(ref _pending) == 0 && Interlocked.Read(ref _dropped) == 0) _idle.Set();
                if (_queue.Reader.Completion.IsCompleted && Interlocked.Read(ref _dropped) == 0) break;
            }
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _enabled, 0);
            _failure("日志写入失败，本组件日志不完整；应用功能可继续使用。", error);
        }
        finally
        {
            Interlocked.Exchange(ref _enabled, 0);
            while (_queue.Reader.TryRead(out _)) { }
            Interlocked.Exchange(ref _pending, 0);
            _idle.Set();
            try { await _file.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private async Task WriteEntryAsync(DiagnosticEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, timeUtc = entry.TimeUtc, sequence = ++_sequence, level = entry.Level.ToString(),
            component = _component, role = _role, pid = Environment.ProcessId, processInstanceId = _instance,
            sessionId = _session.SessionId, ownerEpoch = _session.OwnerEpoch, source = entry.Source,
            eventName = entry.EventName, message = entry.Message, correlationId = entry.CorrelationId,
            threadId = entry.ThreadId, data = entry.Data, exception = entry.Exception
        });
        if (_length != 0 && _length + bytes.Length + 1 > _rotationBytes)
        {
            await _file.DisposeAsync().ConfigureAwait(false);
            _file = OpenPart();
            _length = 0;
        }
        await _file.WriteAsync(bytes).ConfigureAwait(false);
        await _file.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
        _length += bytes.Length + 1;
    }

    private Stream OpenPart()
    {
        var path = Path.Combine(_session.Directory!, $"{_component}.{_role}.{Environment.ProcessId}.{_instance}.{++_part:D4}.jsonl");
        return _streamFactory?.Invoke(path) ?? new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    }

    public void Flush(TimeSpan timeout)
    {
        Wake();
        var elapsed = Stopwatch.StartNew();
        while ((Interlocked.Read(ref _pending) != 0 || Interlocked.Read(ref _dropped) != 0 || !_idle.IsSet) && elapsed.Elapsed < timeout)
            _idle.Wait(TimeSpan.FromMilliseconds(10));
        if ((Interlocked.Read(ref _pending) != 0 || Interlocked.Read(ref _dropped) != 0 || !_idle.IsSet)
            && Interlocked.Exchange(ref _timeoutReported, 1) == 0)
            _failure("日志刷新超时，末尾记录可能不完整；应用功能可继续使用。", new TimeoutException("Diagnostic flush exceeded its bounded wait."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        Wake();
        try
        {
            if (!_pump.Wait(TimeSpan.FromSeconds(2)) && Interlocked.Exchange(ref _timeoutReported, 1) == 0)
                _failure("日志关闭超时，末尾记录可能不完整。", new TimeoutException("Diagnostic shutdown exceeded two seconds."));
        }
        catch { }
        // The pump owns its stream and synchronization primitives until it actually finishes.
    }
}
