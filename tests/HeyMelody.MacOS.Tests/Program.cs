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
                Check(NativeBluetooth.hm_bt_bridge_version() == 2, "native ABI version");
                Check(MacBluetoothPlatform.RunWorker(() => Task.FromResult(23)) == 23, "worker native run loop exits");
            }
            else
            {
                Console.WriteLine("SKIP macOS ARM64 native ABI and worker run loop: this process is not macOS ARM64.");
            }
            TestFailureMapping();
            TestPlatformLifecycle();
            TestStartupFailures();
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
    private static void TestFailureMapping()
    {
        Check(MacBluetoothPlatform.Failure(-2) is BluetoothDiscoveryException { Failure: BluetoothDiscoveryFailure.ServiceNotFound },
            "missing service maps to discovery service-not-found");
        Check(MacBluetoothPlatform.Failure(-3) is BluetoothDiscoveryException { Failure: BluetoothDiscoveryFailure.AmbiguousServices },
            "multiple services map to discovery ambiguity");
        Check(MacBluetoothPlatform.Failure(-4) is BluetoothAccessException { Access: BluetoothAccess.Denied },
            "denied access maps to Bluetooth access denial");
        Check(MacBluetoothPlatform.Failure(-8) is IOException { Message: var off } && off.Contains("蓝牙已关闭"),
            "powered-off radio keeps its existing message");
        Check(MacBluetoothPlatform.Failure(-9) is IOException { Message: var sdp } && sdp.Contains("查询超时"),
            "SDP timeout has a distinct message");
        Check(MacBluetoothPlatform.Failure(-10) is IOException { Message: var open } && open.Contains("打开超时"),
            "RFCOMM open timeout has a distinct message");
        Check(MacBluetoothPlatform.Failure(-11) is IOException { Message: var busy } && busy.Contains("占用"),
            "exclusive access identifies another host");
        Check(MacBluetoothPlatform.Failure(-12345) is IOException { Message: var unknown } && unknown.Contains("-12345"),
            "unknown status keeps its numeric code");
    }
    private static void TestPlatformLifecycle()
    {
        using var platform = new MacBluetoothPlatform();
        Check(platform.Status.Access == BluetoothAccess.Unknown, "platform starts without probing native Bluetooth");
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            bool rejected = false;
            try { platform.StartAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (PlatformNotSupportedException) { rejected = true; }
            Check(rejected, "public startup keeps the macOS ARM64 guard without loading native Bluetooth");
        }
    }
    private static void TestStartupFailures()
    {
        foreach (var failure in new Exception[] { new DllNotFoundException("missing bridge"),
            new BadImageFormatException("wrong bridge architecture"), new EntryPointNotFoundException("missing version symbol") })
        {
            var platform = new MacBluetoothPlatform();
            int versions = 0, starts = 0;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    platform.StartNativeBridge(() => { versions++; throw failure; }, _ => starts++);
                    throw new InvalidOperationException("A failed version query must not complete startup.");
                }
                catch (Exception actual) when (ReferenceEquals(actual, failure))
                { Check(true, "version query preserves the original native loading failure"); }
            }
            Check(versions == 2 && starts == 0, "version failure resets startup before retry and never invokes native start");
            platform.Dispose();
            platform.Dispose();
            Check(true, "disposing after version failure is safe and does not enter native shutdown");
        }

        var failedStart = new MacBluetoothPlatform();
        var startFailure = new IOException("native start failed");
        int versionCalls = 0, startCalls = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                failedStart.StartNativeBridge(() => { versionCalls++; return 2; }, context =>
                {
                    startCalls++;
                    Check(context != 0 && ReferenceEquals(GCHandle.FromIntPtr(context).Target, failedStart),
                        "native start receives an allocated handle for its platform");
                    throw startFailure;
                });
                throw new InvalidOperationException("A failed native start must not complete startup.");
            }
            catch (Exception actual) when (ReferenceEquals(actual, startFailure))
            { Check(true, "start failure preserves the original exception after releasing its handle"); }
        }
        Check(versionCalls == 2 && startCalls == 2, "start failure resets startup before retry");
        failedStart.Dispose();
        failedStart.Dispose();
        Check(true, "disposing after native start failure does not call native shutdown");
        int disposedCalls = 0;
        try { failedStart.StartNativeBridge(() => { disposedCalls++; return 2; }, _ => disposedCalls++); }
        catch (ObjectDisposedException) { Check(disposedCalls == 0, "disposed platform never re-enters injected startup"); return; }
        throw new InvalidOperationException("A disposed platform must reject startup.");
    }
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
