#if WINDOWS
using HeyMelody.Platform;
using HeyMelodyWindows.Windows;
using Windows.Devices.Geolocation;
#endif

internal static class WindowsLocationPermissionTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
#if WINDOWS
        await test("Windows idle permission checks and explicit consent create no location request", async () =>
        {
            var tracker = new RequestTracker();
            int permissions = 0;
            using var provider = new WindowsLocationProvider(tracker.Create, _ =>
            {
                permissions++;
                return Task.FromResult(GeolocationAccessStatus.Allowed);
            });
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Unknown);
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Unknown);
            Check((await provider.RequestPermissionAsync(default)).Access == LocationAccess.Allowed);
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Allowed);
            Check(permissions == 1 && tracker.Requests.Count == 0 && tracker.Active == 0);
        });

        await test("Windows cold permission is verified by one disconnect request and released after success", async () =>
        {
            var tracker = new RequestTracker { Next = Task.FromResult(Fix()) };
            int permissions = 0;
            using var provider = new WindowsLocationProvider(tracker.Create, _ =>
            {
                permissions++;
                return Task.FromResult(GeolocationAccessStatus.Allowed);
            });
            Check((await provider.GetLocationAsync(default)) == Fix());
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Allowed);
            Check(permissions == 0 && tracker.Requests.Count == 1 && tracker.Active == 0);
            Check(tracker.Requests[0].Disposed && tracker.Requests[0].Timeout == TimeSpan.FromSeconds(15));
            await provider.CheckPermissionAsync(default);
            Check(tracker.Requests.Count == 1);
        });

        await test("Windows cancellation disposes a pending request even if its task ignores cancellation", async () =>
        {
            var completion = Pending();
            var tracker = new RequestTracker { Next = completion.Task };
            using var provider = new WindowsLocationProvider(tracker.Create, Allow);
            using var cancellation = new CancellationTokenSource();
            var pending = provider.GetLocationAsync(cancellation.Token);
            Check(tracker.Active == 1);
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(pending);
            Check(tracker.Active == 0 && tracker.Requests[0].Disposed);
            completion.SetResult(Fix());
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Unknown);
        });

        await test("Windows acquisition timeout cancels and releases its single request", async () =>
        {
            var tracker = new RequestTracker { Next = Pending().Task };
            using var provider = new WindowsLocationProvider(tracker.Create, Allow, TimeSpan.FromMilliseconds(50));
            await ExpectAsync<TimeoutException>(provider.GetLocationAsync(default));
            Check(tracker.Active == 0 && tracker.Requests.Count == 1 && tracker.Requests[0].Disposed);
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Unknown);
        });

        await test("Windows failure and invalid fixes release acquisition without inventing permission", async () =>
        {
            var tracker = new RequestTracker { Next = Task.FromException<LocationFix>(new IOException("No fix.")) };
            using var provider = new WindowsLocationProvider(tracker.Create, Allow);
            await ExpectAsync<IOException>(provider.GetLocationAsync(default));
            Check(tracker.Active == 0 && tracker.Requests[0].Disposed);
            tracker.Next = Task.FromResult(Fix() with { Latitude = double.NaN });
            await ExpectAsync<InvalidOperationException>(provider.GetLocationAsync(default));
            Check(tracker.Active == 0 && tracker.Requests[1].Disposed);
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Unknown);
        });

        await test("Windows denied acquisition pauses without a monitor and explicit consent can recover", async () =>
        {
            var tracker = new RequestTracker
            {
                Next = Task.FromException<LocationFix>(new UnauthorizedAccessException("Denied."))
            };
            using var provider = new WindowsLocationProvider(tracker.Create, Allow);
            await ExpectAsync<UnauthorizedAccessException>(provider.GetLocationAsync(default));
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Denied && tracker.Active == 0);
            await ExpectAsync<UnauthorizedAccessException>(provider.GetLocationAsync(default));
            Check(tracker.Requests.Count == 1);
            Check((await provider.RequestPermissionAsync(default)).Access == LocationAccess.Allowed);
            tracker.Next = Task.FromResult(Fix());
            await provider.GetLocationAsync(default);
            Check(tracker.Requests.Count == 2 && tracker.Active == 0);
        });

        await test("Windows provider disposal cancels all active native request owners", async () =>
        {
            var tracker = new RequestTracker { Next = Pending().Task };
            var provider = new WindowsLocationProvider(tracker.Create, Allow);
            var first = provider.GetLocationAsync(default);
            var second = provider.GetLocationAsync(default);
            Check(tracker.Active == 2);
            provider.Dispose();
            Check(tracker.Active == 0 && tracker.Requests.All(value => value.Disposed));
            await ExpectAsync<OperationCanceledException>(first);
            await ExpectAsync<OperationCanceledException>(second);
            provider.Dispose();
            Check(tracker.Active == 0);
        });

        await test("a late Windows denial cannot overwrite a newer explicit authorization", async () =>
        {
            var completion = Pending();
            var tracker = new RequestTracker { Next = completion.Task };
            using var provider = new WindowsLocationProvider(tracker.Create, Allow);
            var old = provider.GetLocationAsync(default);
            Check((await provider.RequestPermissionAsync(default)).Access == LocationAccess.Allowed);
            completion.SetException(new UnauthorizedAccessException("Old denial."));
            await ExpectAsync<UnauthorizedAccessException>(old);
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Allowed && tracker.Active == 0);
        });

        await test("a late Windows denial cannot overwrite a newer successful collection", async () =>
        {
            var completion = Pending();
            var tracker = new RequestTracker { Next = completion.Task };
            using var provider = new WindowsLocationProvider(tracker.Create, Allow);
            var old = provider.GetLocationAsync(default);
            tracker.Next = Task.FromResult(Fix());
            await provider.GetLocationAsync(default);
            completion.SetException(new UnauthorizedAccessException("Old denial."));
            await ExpectAsync<UnauthorizedAccessException>(old);
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Allowed && tracker.Active == 0);
        });

        await test("Windows permission listeners cannot receive an older value after reentrant consent", async () =>
        {
            var tracker = new RequestTracker();
            using var provider = new WindowsLocationProvider(tracker.Create,
                _ => Task.FromResult(GeolocationAccessStatus.Denied));
            var observed = new List<LocationAccess>();
            provider.PermissionChanged += permission =>
            {
                if (permission.Access == LocationAccess.Allowed)
                    provider.RequestPermissionAsync(default).GetAwaiter().GetResult();
            };
            provider.PermissionChanged += permission => observed.Add(permission.Access);
            await provider.GetLocationAsync(default);
            Check(observed.SequenceEqual([LocationAccess.Denied]));
            Check((await provider.CheckPermissionAsync(default)).Access == LocationAccess.Denied && tracker.Active == 0);
        });
#else
        await Task.CompletedTask;
#endif
    }

#if WINDOWS
    private static Task<GeolocationAccessStatus> Allow(CancellationToken _) => Task.FromResult(GeolocationAccessStatus.Allowed);
    private static LocationFix Fix() => new(22.3, 114.2, 35, DateTimeOffset.Parse("2026-11-14T00:00:00Z"));
    private static TaskCompletionSource<LocationFix> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Windows on-demand location regression.");
    }
    private static async Task ExpectAsync<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class RequestTracker
    {
        internal Task<LocationFix> Next = Task.FromResult(Fix());
        internal readonly List<FakeRequest> Requests = [];
        internal int Active;
        internal IWindowsLocationRequest Create(TimeSpan timeout)
        {
            var request = new FakeRequest(this, timeout, Next);
            Requests.Add(request);
            Active++;
            return request;
        }
    }
    private sealed class FakeRequest(RequestTracker tracker, TimeSpan timeout, Task<LocationFix> completion) : IWindowsLocationRequest
    {
        internal TimeSpan Timeout { get; } = timeout;
        internal bool Disposed { get; private set; }
        public Task<LocationFix> GetLocationAsync(CancellationToken token) => completion;
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Interlocked.Decrement(ref tracker.Active);
        }
    }
#endif
}
