using System.Runtime.InteropServices;
using HeyMelody.MacOS;
using HeyMelody.Platform;

internal static class Program
{
    private static int _checks;
    public static int Main()
    {
        try
        {
            // Loads the native ABI and pumps the worker loop without creating a Bluetooth manager.
            if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                Check(NativeBluetooth.hm_bt_bridge_version() == 1, "native ABI version");
                Check(MacBluetoothPlatform.RunWorker(() => Task.FromResult(23)) == 23, "worker native run loop exits");
            }
            RunAsync().GetAwaiter().GetResult();
            TestObservationGenerations();
            Console.WriteLine($"PASS {_checks} macOS offline checks (no Bluetooth manager, pairing, connection or device writes).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task RunAsync()
    {
        int closes = 0;
        using (var stream = new MacRfcommStream((_, _) => Task.CompletedTask, () => closes++))
        {
            stream.Receive([1, 2, 3]); stream.Receive([4, 5]);
            byte[] bytes = new byte[2];
            Check(await stream.ReadAsync(bytes) == 2 && bytes.SequenceEqual(new byte[] { 1, 2 }), "fragment first native callback");
            Check(await stream.ReadAsync(bytes) == 1 && bytes[0] == 3, "retain partial native callback");
            Check(await stream.ReadAsync(bytes) == 2 && bytes.SequenceEqual(new byte[] { 4, 5 }), "read next callback in order");
            stream.Complete(null);
            Check(await stream.ReadAsync(bytes) == 0, "remote close EOF");
            await Throws<IOException>(() => stream.WriteAsync(new byte[] { 9 }).AsTask(), "writes fail after remote close");
        }
        Check(closes == 1, "dispose closes once");
        using (var stream = new MacRfcommStream((_, _) => Task.CompletedTask, () => { }))
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => stream.ReadAsync(new byte[1], cancellation.Token).AsTask(), "cancel pending read");
            stream.Receive([7]);
            Check(await stream.ReadAsync(new byte[1]) == 1, "canceled reader does not consume bytes");
        }
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = new List<byte>();
        using (var stream = new MacRfcommStream(async (bytes, token) =>
        {
            lock (submissions) submissions.Add(bytes.Span[0]);
            if (bytes.Span[0] == 1) { firstEntered.SetResult(); await releaseFirst.Task.WaitAsync(token); }
        }, () => { }))
        {
            var first = stream.WriteAsync(new byte[] { 1 }).AsTask(); await firstEntered.Task;
            using var cancellation = new CancellationTokenSource();
            var canceled = stream.WriteAsync(new byte[] { 2 }, cancellation.Token).AsTask(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => canceled, "cancel queued write before submission");
            var next = stream.WriteAsync(new byte[] { 3 }).AsTask();
            Check(submissions.SequenceEqual(new byte[] { 1 }), "serialize writes");
            releaseFirst.SetResult(); await Task.WhenAll(first, next);
            Check(submissions.SequenceEqual(new byte[] { 1, 3 }), "canceled queued write is never replayed");
        }
        closes = 0;
        using (var stream = new MacRfcommStream((_, _) => Task.CompletedTask, () => closes++))
        {
            stream.Receive(new byte[1024 * 1024 + 1]);
            await Throws<IOException>(() => stream.ReadAsync(new byte[1]).AsTask(), "receive overflow closes instead of losing protocol bytes");
            Check(closes == 1, "receive overflow closes native channel");
        }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); _checks++; }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(message);
    }
    private static void TestObservationGenerations()
    {
        var gate = new BluetoothObservationGate();
        var nativeTime = DateTimeOffset.FromUnixTimeMilliseconds(1234567890000);
        var hostTime = nativeTime.AddTicks(9000); // Same millisecond, greater precision than native JSON.
        var request = gate.SetDemand(true, hostTime);
        Check(!request.Wanted, "unavailable adapter never requests scan");
        request = gate.SetAvailable(true, hostTime);
        BluetoothAdvertisementReceived Advertisement(long generation, long requestGeneration, DateTimeOffset? timestamp = null, short rssi = -30)
            => new(0x079A, [1], 0, false, timestamp ?? nativeTime, rssi) { Generation = generation, RequestGeneration = requestGeneration };
        gate.ObserveScan(new(true, 1, request.Generation, nativeTime));
        Check(gate.Accept(Advertisement(1, request.Generation)), "millisecond precision admits current scan observations");
        Check(!gate.Accept(Advertisement(0, 0)), "macOS does not accept unversioned observations");
        var oldRequest = request;
        gate.SetDemand(false, hostTime);
        request = gate.SetDemand(true, hostTime);
        gate.ObserveScan(new(true, 1, oldRequest.Generation, nativeTime));
        Check(!gate.Accept(Advertisement(1, oldRequest.Generation)), "pending old start cannot reopen old scan after same-ms demand restart");
        gate.ObserveScan(new(true, 2, request.Generation, nativeTime));
        Check(gate.Accept(Advertisement(2, request.Generation)), "new request and native scan generation accepted");
        gate.ObserveScan(new(false, 1, oldRequest.Generation, nativeTime));
        Check(gate.Accept(Advertisement(2, request.Generation)), "late old stop cannot retire current scan");
        gate.ObserveScan(new(false, 2, request.Generation, nativeTime));
        gate.ObserveScan(new(true, 2, request.Generation, nativeTime));
        Check(!gate.Accept(Advertisement(2, request.Generation)), "retired native generation cannot reopen");
        gate.ObserveScan(new(true, 3, request.Generation, nativeTime));
        Check(gate.Accept(Advertisement(3, request.Generation)), "replacement scanner in current request accepted");
        oldRequest = request; gate.SetSuspended(true, hostTime);
        Check(!gate.Accept(Advertisement(3, oldRequest.Generation)), "suspended observations rejected immediately");
        request = gate.SetSuspended(false, hostTime);
        gate.ObserveScan(new(true, 3, oldRequest.Generation, nativeTime));
        Check(!gate.Accept(Advertisement(3, oldRequest.Generation)), "pre-wake source rejected");
        gate.ObserveScan(new(true, 4, request.Generation, nativeTime));
        Check(gate.Accept(Advertisement(4, request.Generation)), "post-wake source accepted");
        Check(!gate.Accept(Advertisement(4, request.Generation, nativeTime.AddMilliseconds(-1))), "pre-request timestamp rejected");
        Check(!gate.Accept(Advertisement(4, request.Generation, rssi: -127)), "invalid RSSI rejected");
        gate.SetAvailable(false, hostTime);
        Check(!gate.Accept(Advertisement(4, request.Generation)), "power-off observations rejected immediately");
        var windows = new BluetoothObservationGate(acceptLegacyWatcher: true);
        windows.SetAvailable(true, nativeTime); windows.SetDemand(true, nativeTime);
        Check(windows.Accept(Advertisement(0, 0)), "Windows native watcher keeps its existing generation validation boundary");
        Check(!windows.Accept(Advertisement(0, 0, nativeTime.AddMilliseconds(-1))), "Windows timestamp gate rejects old observations");
    }
}
