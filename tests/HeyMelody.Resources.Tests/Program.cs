using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.Resources;

var tests = new (string Name, Func<Task> Run)[]
{
    ("The complete HeyMelody catalog and artwork load", Bundles),
    ("Compatibility honors non-null zero and empty lists", Compatibility),
    ("Child expansion matches the official inheritance whitelist", Children),
    ("Whole snapshot selection respects content version", Selection),
    ("Duplicate JSON keys and malformed identifiers are rejected", InvalidCatalogs),
    ("HMAC signs the exact UTF-8 request including raw custom key", Signature),
    ("Cache envelope authenticates data and rejects tampering", Cache),
    ("Plaintext keys and offline preference survive restart", Preferences),
    ("Legacy selections preserve HeyMelody settings and cached resources", LegacyPreferences),
    ("Online update picks strictly higher content and deletes older online cache", Versions),
    ("Failed download keeps the effective catalog and cache", FailedUpdate),
    ("Online off avoids every request and reuses downloaded content", Offline),
    ("Unchanged metadata repairs a damaged cache without publishing equal content", CacheRepair),
    ("Art publication preserves leases and cleans retired online packages", ArtworkLeases),
    ("Hash failures and unsafe archives retain current art", UnsafeArtwork),
    ("Known product identity and color are required for model artwork", ArtworkIdentity),
    ("Model metadata without PID is bound to request and validates GLB", ModelPackages),
    ("Model WebP is obtained without a 3D flag, stays animated offline and rejects damaged replacements", AnimatedModelPackages),
    ("Cached art is served before any network check and refreshed in the background", CachedFirst),
    ("HTTP timeouts fall back to installed art and are throttled like failures", TimeoutFallback),
    ("A first download is awaited briefly and its late package is announced", FirstDownload),
    ("Lottie tutorials retain provenance and never replace the product hero", LottieLists),
    ("Re-extraction publishes beside a leased copy of the same digest", LeasedRepublish),
    ("Packages from an older extractor are re-downloaded once and served until then", ExtractorUpgrade),
    ("Re-download validates replacements, keeps files on failure and requires online use", Redownload),
    ("Startup removes interrupted downloads and superseded packages", StartupSweep),
    ("Streaming SHA-256 matches whole-file hashing at every buffer boundary", StreamingHash),
    ("Oversized bodies without Content-Length fail early, throttle and leave no scratch directory or new slot", OversizedDownload),
    ("Digest mismatch, unsafe archives and cancellation leave no scratch file, extraction directory or new slot", FailedDownloadResidue),
    ("A one-byte tamper drops the lease to the fallback and a forced check re-downloads", TamperedLease),
    ("Damaged GLB, vfxms and WebP members are skipped without failing the package", DamagedMembers),
    ("A vfxms slice is copied byte for byte to model.glb", VfxmsSlice),
    ("An older response still repairs a damaged cache from the retained bytes", OlderResponseRepair),
    ("The bundled catalog is shared and only online snapshots retain their bytes", SharedBundledCatalog),
    ("Auto refresh makes no request and holds no timer while online resources are off, then checks at once when they are turned on", AutoRefreshOffline),
    ("Auto refresh re-checks a success after 10 minutes and retries a failure after 30 seconds", AutoRefreshSchedule),
    ("Auto refresh reports an unexpected failure and retries after the 30-second floor instead of stopping", AutoRefreshUnexpectedFailure),
    ("Auto refresh wakes for artwork due times and an added target", AutoRefreshArtwork),
    ("A preference change clears the throttles but waits out the 30-second pass floor", AutoRefreshFloor),
    ("Disposing the coordinator ends auto refresh within two seconds, sleeping or not", AutoRefreshDispose),
    ("Rescheduling after the clock moved past a due check (system sleep) runs it at once, and does nothing when none is due", AutoRefreshReschedule),
};
if (args is ["--verify-x4-artwork", var controlPackage, var modelPackage])
    tests = [.. tests, ("Real X4 packages resolve animated product art and static fallback from HeyMelody", () => RealX4Artwork(controlPackage, modelPackage))];
int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} resource tests passed");
return failed == 0 ? 0 : 1;

static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
static void Eq<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static Task Done(Action action) { action(); return Task.CompletedTask; }
static byte[] Config(int version, string list = "[{\"id\":\"06EC10\",\"name\":\"OPPO Enco X4\",\"brand\":\"oppo\",\"type\":\"T1\",\"supportSpp\":true,\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\",\"function\":{\"batteryInfo\":1}}]")
    => Encoding.UTF8.GetBytes("{\"versionCode\":" + version + ",\"whiteList\":" + list + "}");
static Task Bundles() => Done(() =>
{
    var store = new BundledResourceStore(); var hey = store.GetCatalog();
    Eq(85, hey.Profiles.Count); Eq(117006004, hey.ContentVersion);
    Check(hey.Find("49074B") is null, "Speaker was included");
    Check(hey.Find("06EC10")!.CanUseControlTransport, "X4 transport missing");
    Eq(365, store.Artwork.Count);
    foreach (var asset in store.Artwork)
    {
        string file = store.FullPath(asset); Check(File.Exists(file), "Missing bundled art"); Eq(asset.Length, new FileInfo(file).Length);
        Eq(asset.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        if (asset.Kind == "lottie")
        {
            using var animation = JsonDocument.Parse(File.ReadAllBytes(file));
            if (animation.RootElement.TryGetProperty("assets", out var images)) foreach (var image in images.EnumerateArray())
                if (image.TryGetProperty("p", out var p) && !p.GetString()!.StartsWith("data:", StringComparison.Ordinal))
                    Check(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, image.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "", p.GetString()!)), "Bundled Lottie image missing");
        }
    }
    Eq(24, store.GetDefaultKey().Length);
});
static Task Compatibility() => Done(() =>
{
    var json = Encoding.UTF8.GetBytes("{\"versionCode\":50,\"compatVersionCode\":0,\"whiteList\":[{\"id\":\"06EC10\",\"brand\":\"oppo\"}],\"compatWhiteList\":[],\"caseNumSite\":5,\"compatCaseNumSite\":0,\"appMarket\":\"a\",\"compatAppMarket\":\"\"}");
    var parsed = ResourceCatalog.Parse(json, true); Eq(0, parsed.ContentVersion); Eq(0, parsed.Profiles.Count);
    Eq(0, parsed.Raw.GetProperty("caseNumSite").GetInt32()); Eq("", parsed.Raw.GetProperty("appMarket").GetString());
    parsed = ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":50,\"compatVersionCode\":null,\"whiteList\":[],\"compatWhiteList\":null}"), true); Eq(50, parsed.ContentVersion);
});
static Task Children() => Done(() =>
{
    string list = "[{\"id\":\"060414\",\"name\":\"parent\",\"brand\":\"oneplus\",\"type\":\"T1\",\"supportSpp\":true,\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\",\"function\":{\"equalizer\":4},\"children\":[{\"name\":\"alias\"},{\"id\":\"060814\",\"name\":\"child\",\"uuid\":\"\",\"function\":{\"equalizer\":99},\"supportSpp\":false}]}]";
    var parsed = ResourceCatalog.Parse(Config(1, list), false); Eq(2, parsed.Profiles.Count);
    var child = parsed.Find("060814")!; Eq("child", child.Name); Check(child.SupportSpp, "Arbitrary child field replaced parent");
    Eq(4, child.Functions.GetProperty("equalizer").GetInt32()); Eq(1, child.Functions.GetProperty("hideFromUIList").GetInt32());
    Check(!child.Raw.TryGetProperty("children", out _), "Child kept nested children");
});
static Task Selection() => Done(() =>
{
    var bundled = ResourceCatalog.Parse(Config(5), false);
    foreach (int version in new[] { 3, 5, 6 })
    {
        var online = ResourceCatalog.Parse(Config(version), true);
        Check(ReferenceEquals(version > 5 ? online : bundled, ResourceCatalog.Select(bundled, online)), "Whole snapshot precedence wrong");
    }
});
static Task InvalidCatalogs() => Done(() =>
{
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":1,\"versionCode\":2,\"whiteList\":[]}"), true));
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Encoding.UTF8.GetBytes("{\"versionCode\":true,\"whiteList\":[]}"), true));
    Throws<InvalidDataException>(() => ResourceCatalog.Parse(Config(1, "[{\"id\":\"06ec10\",\"brand\":\"oppo\"}]"), true));
    var profile = ResourceCatalog.Parse(Config(1, "[{\"id\":\"06EC10\",\"brand\":\"oppo\",\"uuid\":\"0000079A-D102-11E1-9B23-00025B00A5A5\"}]"), true).Find("06EC10")!;
    Check(!profile.SupportSpp && !profile.CanUseControlTransport, "Missing SPP flag guessed true");
});
static Task Signature() => Done(() =>
{
    byte[] body = VendorResourceClient.CreateRequestBody();
    Eq("{\"platform\":\"android\",\"channel\":\"2\",\"versionCode\":\"17006004\"}", Encoding.UTF8.GetString(body));
    string rawKey = "明文-custom-key";
    using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(rawKey)); Eq(Convert.ToHexStringLower(hmac.ComputeHash(body)), VendorResourceClient.Sign(rawKey, body));
    var model = JsonDocument.Parse(VendorResourceClient.CreateRequestBody("067410", 2, true)).RootElement;
    Eq("14", model.GetProperty("moduleId").GetString()); Eq("17006004", model.GetProperty("versionCode").GetString());
    Eq("2", model.GetProperty("channel").GetString());
    Throws<InvalidDataException>(() => VendorResourceClient.ValidateDownloadUri("https://evil.example/resource"));
});
static Task Cache() => Done(() =>
{
    byte[] key = RandomNumberGenerator.GetBytes(32), original = Config(10), encrypted = ResourceCacheCodec.Encrypt(original, key);
    Check(ResourceCacheCodec.Decrypt(encrypted, key).SequenceEqual(original), "Cache changed bytes"); encrypted[^1] ^= 1;
    Throws<AuthenticationTagMismatchException>(() => ResourceCacheCodec.Decrypt(encrypted, key));
    Throws<AuthenticationTagMismatchException>(() => ResourceCacheCodec.Decrypt(ResourceCacheCodec.Encrypt(original, key), RandomNumberGenerator.GetBytes(32)));
    Throws<InvalidDataException>(() => ResourceCacheCodec.Encrypt(new byte[ResourceLimits.ConfigBytes + 1], key));
});
static async Task Preferences()
{
    using var fixture = new Fixture(); using var first = fixture.Coordinator(); await first.InitializeAsync();
    Check(first.Preferences.UseOnlineResources, "Online should default on");
    string encryption = first.Preferences.CacheEncryptionKey;
    await first.UpdatePreferencesAsync(first.Preferences with { UseOnlineResources = false, HeyMelodyKey = "user-hey" });
    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Path, "preferences.json")));
    Eq("user-hey", saved.RootElement.GetProperty("HeyMelodyKey").GetString());
    Check(!saved.RootElement.TryGetProperty("Source", out _) && !saved.RootElement.TryGetProperty("WirelessEarphonesKey", out _), "Removed preferences were written");
    Eq(encryption, saved.RootElement.GetProperty("CacheEncryptionKey").GetString());
    using var second = fixture.Coordinator(); await second.InitializeAsync(); Eq(encryption, second.Preferences.CacheEncryptionKey);
    Check(!second.Preferences.UseOnlineResources, "Offline preference lost"); Eq(0, fixture.Handler.Requests);
}
static async Task LegacyPreferences()
{
    foreach (int legacySelection in new[] { 0, 1 })
    {
        using var fixture = new Fixture();
        byte[] key = RandomNumberGenerator.GetBytes(32), cached = Config(117006009), ignored = Config(999999999);
        string encryption = Convert.ToBase64String(key);
        Directory.CreateDirectory(fixture.Path);
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "preferences.json"), JsonSerializer.Serialize(new
        {
            Source = legacySelection, UseOnlineResources = false, HeyMelodyKey = "user-hey",
            WirelessEarphonesKey = "retired-key", CacheEncryptionKey = encryption
        }));
        async Task<string> Seed(string directory, byte[] bytes)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string path = Path.Combine(fixture.Path, "online", directory, "config", digest, "config.cache");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, ResourceCacheCodec.Encrypt(bytes, key));
            return path;
        }
        await Seed("heymelody", cached);
        string ignoredCache = await Seed("wireless", ignored);
        using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
        Eq("user-hey", coordinator.Preferences.HeyMelodyKey); Eq(encryption, coordinator.Preferences.CacheEncryptionKey);
        Check(!coordinator.Preferences.UseOnlineResources, "Legacy offline preference lost");
        Eq(117006009, coordinator.Catalog.ContentVersion); Check(coordinator.Catalog.IsOnline, "Encrypted HeyMelody cache was discarded");
        Check(File.Exists(ignoredCache), "Unrelated legacy cache was mutated");
        Check(!await coordinator.RefreshAsync(true), "Offline migration attempted an update"); Eq(0, fixture.Handler.Requests);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Path, "preferences.json")));
        Check(!saved.RootElement.TryGetProperty("Source", out _) && !saved.RootElement.TryGetProperty("WirelessEarphonesKey", out _), "Legacy fields survived migration");
    }
}
static async Task Versions()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { HeyMelodyKey = "test-key" });
    fixture.Handler.Configuration = Config(117006010); fixture.Handler.FileVersion = "90"; Check(await coordinator.RefreshAsync(true), "New config not adopted");
    string first = coordinator.Catalog.Sha256; Eq(1, Directory.GetDirectories(Path.Combine(fixture.Path, "online/heymelody/config")).Length);
    fixture.Handler.Configuration = Config(117006011); fixture.Handler.FileVersion = "80"; Check(await coordinator.RefreshAsync(true), "Changed metadata compared numerically");
    Check(!Directory.Exists(Path.Combine(fixture.Path, "online/heymelody/config", first)), "Old online config not removed"); Eq(117006011, coordinator.Catalog.ContentVersion);
    fixture.Handler.Configuration = Config(117006011); fixture.Handler.FileVersion = "81"; Check(!await coordinator.RefreshAsync(true), "Equal content replaced current");
    fixture.Handler.Configuration = Config(117006009); fixture.Handler.FileVersion = "82"; Check(!await coordinator.RefreshAsync(true), "Older content adopted"); Eq(117006011, coordinator.Catalog.ContentVersion);
    Eq("test-key", fixture.Handler.LastVerifiedKey); Eq(1, Directory.GetDirectories(Path.Combine(fixture.Path, "online/heymelody/config")).Length);
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false }); Eq(117006011, coordinator.Catalog.ContentVersion);
}
static async Task FailedUpdate()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Configuration = Config(117006010); Check(await coordinator.RefreshAsync(true), "Initial update failed"); var catalog = coordinator.Catalog;
    fixture.Handler.FileVersion = "changed"; fixture.Handler.Configuration = Encoding.UTF8.GetBytes("{broken"); Check(!await coordinator.RefreshAsync(true), "Malformed config accepted"); Check(ReferenceEquals(catalog, coordinator.Catalog), "Failure changed catalog");
    Check(File.Exists(Path.Combine(fixture.Path, "online/heymelody/config", catalog.Sha256, "config.cache")), "Failure removed previous cache"); Check(coordinator.LastError is not null, "Failure not reported");
}
static async Task Offline()
{
    using var fixture = new Fixture();
    using (var coordinator = fixture.Coordinator()) { await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010); await coordinator.RefreshAsync(true); await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false }); }
    int before = fixture.Handler.Requests;
    using var fresh = fixture.Coordinator(); await fresh.InitializeAsync(); Eq(117006010, fresh.Catalog.ContentVersion); Check(!await fresh.RefreshAsync(true), "Offline made config request");
    using var art = await fresh.ResolveArtworkAsync("06EC10", 0, "detail"); Check(art is not null && art.Kind == "bitmap", "Offline fallback missing"); Eq(before, fixture.Handler.Requests);
}
static async Task ArtworkLeases()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = ArtZip(1); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "First art failed");
    var lease = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Check(lease is not null, "No art lease"); string old = lease!.PackageRoot;
    fixture.Handler.Artwork = ArtZip(2); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "New art failed"); Check(Directory.Exists(old), "Active renderer lost old resource");
    using var newer = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Check(newer is not null && newer.PackageRoot != old, "New pointer not published"); lease.Dispose(); Check(!Directory.Exists(old), "Released old online package not cleaned");
    using var installed = coordinator.Bundled.Acquire(coordinator.Bundled.FindAsset("06EC10", null, "detail")); Check(installed is not null && File.Exists(installed.FullPath), "Bundled art removed");
}
static async Task CacheRepair()
{
    using var fixture = new Fixture();
    using (var coordinator = fixture.Coordinator())
    {
        await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010); await coordinator.RefreshAsync(true);
        string path = Path.Combine(fixture.Path, "online/heymelody/config", coordinator.Catalog.Sha256, "config.cache");
        byte[] broken = await File.ReadAllBytesAsync(path); broken[^1] ^= 1; await File.WriteAllBytesAsync(path, broken);
        long revision = coordinator.Revision; Check(!await coordinator.RefreshAsync(true), "Equal content published"); Eq(revision, coordinator.Revision);
        Check(ResourceCacheCodec.Decrypt(await File.ReadAllBytesAsync(path), Convert.FromBase64String(coordinator.Preferences.CacheEncryptionKey)).SequenceEqual(fixture.Handler.Configuration), "Cache was not repaired");
    }
    using var fresh = fixture.Coordinator(); await fresh.InitializeAsync(); Eq(117006010, fresh.Catalog.ContentVersion);
}
static async Task UnsafeArtwork()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); await coordinator.RefreshArtworkAsync("06EC10", 0, force: true);
    using var before = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    fixture.Handler.Artwork = ArtZip(2); fixture.Handler.WrongHash = true; Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Wrong hash accepted"); Check(File.Exists(before!.FullPath), "Hash failure deleted art");
    fixture.Handler.WrongHash = false; fixture.Handler.Artwork = ArtZip(3, "../escaped.png"); Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Traversal archive accepted"); Check(!File.Exists(Path.Combine(fixture.Path, "escaped.png")), "Archive escaped cache");
    using var after = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Eq(before.PackageRoot, after!.PackageRoot);
}
static Task ArtworkIdentity() => Done(() =>
{
    var store = new BundledResourceStore(); Check(store.FindAsset("068C10", 1, "model") is not null, "Verified Free4 model missing");
    Check(store.FindAsset("068C10", null, "model") is null, "Model color guessed"); Check(store.FindAsset("06C010", 1, "model") is null, "Free4 model applied to Dynaudio"); Check(store.FindAsset("06EC10", 1, "model") is null, "Free4 model applied to X4");
    Check(store.FindAsset("060410", 0, "detail") is null, "Removed artwork remained in the bundle");
    foreach (string shape in new[] { "T1", "N", "O1" }) Check(store.FindGenericAsset(shape, "hero") is not null, "Generic shape fallback missing");
});
static async Task ModelPackages()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var verified = coordinator.Bundled.FindAsset("068C10", 1, "model")!;
    fixture.Handler.Artwork = ModelZip(File.ReadAllBytes(coordinator.Bundled.FullPath(verified)));
    Check(await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "Valid self-contained model rejected");
    using var lease = await coordinator.ResolveArtworkAsync("068C10", 1, "model"); Check(lease is not null && lease.Kind == "model", "Request-only model association was lost"); Eq(verified.Sha256, lease!.Sha256);
    byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"https://external.example/geometry.bin\"}]} ");
    byte[] glb = new byte[20 + json.Length]; "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)json.Length); "JSON"u8.CopyTo(glb.AsSpan(16)); json.CopyTo(glb, 20);
    fixture.Handler.Artwork = ModelZip(glb); Check(!await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "External model URI accepted"); Check(File.Exists(lease.FullPath), "Rejected model removed previous valid package");
}
static async Task CachedFirst()
{
    using var fixture = new Fixture();
    using (var first = fixture.Coordinator()) { await first.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); Check(await first.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed"); }
    fixture.Handler.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    int before = fixture.Handler.Requests;
    using var art = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail").WaitAsync(TimeSpan.FromSeconds(5));
    Check(art is not null && art.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Cached online art was not served first");
    for (int attempt = 0; fixture.Handler.Pending == 0 && attempt < 200; attempt++) await Task.Delay(10);
    Check(fixture.Handler.Pending == 1 && fixture.Handler.Requests == before + 1, "Background check did not start");
    fixture.Handler.Gate.TrySetResult();
}
static async Task AnimatedModelPackages()
{
    byte[] animation = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-frame.webp"));
    {
        using var fixture = new Fixture();
        fixture.Handler.Artwork = LottieZip(); fixture.Handler.ModelArtwork = ModelWebpZip(animation);
        string root;
        using (var coordinator = fixture.Coordinator())
        {
            await coordinator.InitializeAsync();
            Check(!coordinator.Catalog.Find("06EC10")!.Functions.TryGetProperty("support3DModel", out _), "Fixture no longer exercises an appearance without the 3D flag");
            using var hero = await coordinator.ResolveArtworkAsync("06EC10", 1, "hero");
            Check(hero is { Kind: "animated-webp" }, "Module 14 WebP was skipped or frozen as a bitmap");
            root = hero!.PackageRoot;
            using var detail = await coordinator.ResolveArtworkAsync("06EC10", 1, "detail");
            Check(detail is { Kind: "bitmap" } && detail.PackageRoot != root, "Control package did not provide the static fallback");
            var broken = (byte[])animation.Clone(); broken[4] ^= 1; // RIFF size no longer matches the downloaded member.
            fixture.Handler.ModelArtwork = ModelWebpZip(broken);
            Check(!await coordinator.RefreshArtworkAsync("06EC10", 1, model: true, force: true), "Malformed animated container replaced valid art");
            using var retained = await coordinator.ResolveArtworkAsync("06EC10", 1, "hero");
            Eq(root, retained!.PackageRoot);
            await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
        }
        int requests = fixture.Handler.Requests;
        using var restarted = fixture.Coordinator(); await restarted.InitializeAsync();
        using var offline = await restarted.ResolveArtworkAsync("06EC10", 1, "hero");
        Check(offline is { Kind: "animated-webp" } && offline.PackageRoot == root && fixture.Handler.Requests == requests, "Animated cache was lost on offline restart");
        using var otherColor = await restarted.ResolveArtworkAsync("06EC10", 0, "hero");
        Check(otherColor?.Kind != "animated-webp", "Animation crossed protocol colors");
    }
}
static async Task TimeoutFallback()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Failure = new TaskCanceledException("The request timed out.");
    using var art = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(art is not null && art.Kind == "bitmap" && !art.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Timeout skipped installed fallback");
    Check(coordinator.LastError is not null, "Timeout was not reported as a failure");
    int requests = fixture.Handler.Requests;
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0) && fixture.Handler.Requests == requests, "Timeout was not throttled");
}
static async Task FirstDownload()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    coordinator.FirstDownloadWait = TimeSpan.FromMilliseconds(200);
    var changed = new TaskCompletionSource<ResourceChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.Changed += (_, args) => changed.TrySetResult(args);
    fixture.Handler.Artwork = ArtZip(1); fixture.Handler.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var fallback = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail").WaitAsync(TimeSpan.FromSeconds(5)))
        Check(fallback is not null && !fallback.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Pending download blocked the installed fallback");
    fixture.Handler.Gate.TrySetResult();
    var args = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(args.ArtworkChanged, "Late package was not announced");
    using var online = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(online is not null && online.FullPath.Contains(Path.Combine("online", "heymelody"), StringComparison.Ordinal), "Announced package was not served");
}
static async Task LottieLists()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = LottieZip();
    Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Lottie package rejected");
    using var hero = await coordinator.ResolveArtworkAsync("06EC10", 0, "hero");
    Check(hero is not null && hero.Kind == "bitmap" && hero.FullPath.EndsWith("detail.png", StringComparison.Ordinal), "Gesture tutorial replaced the product hero");
    using var tutorial = await coordinator.ResolveArtworkAsync("06EC10", 0, "lottie");
    Check(tutorial is not null && tutorial.Kind == "lottie" && tutorial.FullPath.EndsWith("data.heymelody.json", StringComparison.Ordinal), "Explicit tutorial request failed");
    using (var derived = JsonDocument.Parse(File.ReadAllBytes(tutorial!.FullPath)))
    {
        var image = derived.RootElement.GetProperty("assets")[0];
        Eq("images/", image.GetProperty("u").GetString()); Eq("img_0.png", image.GetProperty("p").GetString());
    }
    var entries = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(hero!.PackageRoot, "artwork.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var main = entries.Single(e => e.Role == "lottie" && e.Order == 1);
    Check(main.OriginalPath == "res/lottie/main/data.json" && main.OriginalSha256 is { Length: 64 } && main.Metadata?["imagePath"] == "res/lottie/main/images", "Derived animation lost provenance");
    Check(entries.Any(e => e.Role == "lottie" && e.Order == 2 && e.OriginalPath is null) && entries.Any(e => e.Role == "lottie-left"), "Valid list entries missing");
    Check(!entries.Any(e => e.Role == "lottie" && e.Order == 0) && !entries.Any(e => e.Path.Contains("boot", StringComparison.Ordinal)), "Broken or unrelated entries indexed");
    Check(File.Exists(Path.Combine(hero.PackageRoot, "res/lottie/main/data.json")), "Original animation bytes were not kept");
    using var detail = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(detail is not null && detail.Kind == "bitmap" && detail.PackageRoot == hero.PackageRoot, "Static detail fallback missing");
}
static async Task RealX4Artwork(string controlPath, string modelPath)
{
    byte[] control = File.ReadAllBytes(controlPath), model = File.ReadAllBytes(modelPath);
    Eq("c3f1b327c3ad31d57e47250dad3ccf9e1fc25f3cba0dd170df8388e03f3f5411", Convert.ToHexStringLower(SHA256.HashData(control)));
    Eq("1bb49ddc6d0ffe4f1ab9e6282bc72962d15629bd5ef0dc7480ea4903d51ba6e0", Convert.ToHexStringLower(SHA256.HashData(model)));
    {
        using var fixture = new Fixture();
        fixture.Handler.Artwork = control; fixture.Handler.ModelArtwork = model;
        string modelRoot;
        using (var coordinator = fixture.Coordinator())
        {
            await coordinator.InitializeAsync();
            Check(await coordinator.RefreshArtworkAsync("06EC10", 1, force: true), "Real control package rejected");
            Check(await coordinator.RefreshArtworkAsync("06EC10", 1, model: true, force: true), "Real modelWebp package rejected");
            using var hero = await coordinator.ResolveArtworkAsync("06EC10", 1, "hero");
            Check(hero is { Kind: "animated-webp" }, "Real animated product art was not selected");
            modelRoot = hero!.PackageRoot;
            using var detail = await coordinator.ResolveArtworkAsync("06EC10", 1, "detail");
            Check(detail is { Kind: "bitmap" } && detail.FullPath.EndsWith("img_detail.png", StringComparison.Ordinal), "Real static fallback missing");
            await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
        }
        int requests = fixture.Handler.Requests;
        using var restarted = fixture.Coordinator(); await restarted.InitializeAsync();
        using var restored = await restarted.ResolveArtworkAsync("06EC10", 1, "hero");
        Check(restored is { Kind: "animated-webp" } && restored.PackageRoot == modelRoot && fixture.Handler.Requests == requests, "Real animation was not restored offline");
    }
}
static async Task LeasedRepublish()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = ArtZip(1); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
    var leased = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); string old = leased!.PackageRoot;
    fixture.Handler.Configuration = Config(117006004); // equal to the installed catalog: not adopted
    var result = await coordinator.RedownloadAsync([]);
    Check(result.Failed == 0 && result.Updated == 1, "Re-download did not replace the leased digest: " + result);
    using var replacement = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(replacement!.PackageRoot != old && Path.GetFileName(replacement.PackageRoot).StartsWith(Path.GetFileName(old) + "-", StringComparison.Ordinal), "Leased digest was not published beside its copy");
    Check(Directory.Exists(old), "Active renderer lost its package"); leased.Dispose(); Check(!Directory.Exists(old), "Released copy was not cleaned");
}
static async Task ExtractorUpgrade()
{
    using var fixture = new Fixture(); string root;
    using (var first = fixture.Coordinator())
    {
        await first.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); Check(await first.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
        using var art = await first.ResolveArtworkAsync("06EC10", 0, "detail"); root = art!.PackageRoot;
    }
    File.Delete(Path.Combine(root, "package.json"));
    using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); coordinator.Changed += (_, _) => changed.TrySetResult();
    using (var served = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail")) Check(served?.PackageRoot == root, "Old extraction was not served while upgrading");
    await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var upgraded = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(File.Exists(Path.Combine(upgraded!.PackageRoot, "package.json")), "Old extraction was not re-indexed");
}
static async Task Redownload()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Configuration = Config(117006010); fixture.Handler.Artwork = ArtZip(1);
    Check(await coordinator.RefreshAsync(true) && await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Initial resources failed");
    string catalog = coordinator.Catalog.Sha256; int requests = fixture.Handler.Requests;
    var unchanged = await coordinator.RedownloadAsync([("06EC10", 0)]);
    Check(unchanged.Failed == 0 && !unchanged.CatalogChanged && fixture.Handler.Requests >= requests + 4 && coordinator.Catalog.Sha256 == catalog, "Re-download did not re-fetch equal content: " + unchanged);
    using var before = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    fixture.Handler.Failure = new HttpRequestException("offline");
    var failed = await coordinator.RedownloadAsync([("06EC10", 0)]);
    Check(failed.Failed > 0 && coordinator.LastError is not null && File.Exists(before!.FullPath), "Failed re-download removed working files: " + failed);
    using var after = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail"); Eq(before!.PackageRoot, after!.PackageRoot);
    fixture.Handler.Failure = null;
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
    try { await coordinator.RedownloadAsync([]); throw new InvalidOperationException("Offline re-download was accepted"); }
    catch (InvalidOperationException error) when (error.Message.Contains("在线资源已关闭", StringComparison.Ordinal)) { }
}
static async Task StartupSweep()
{
    using var fixture = new Fixture(); string root, slot;
    using (var first = fixture.Coordinator())
    {
        await first.InitializeAsync(); fixture.Handler.Artwork = ArtZip(1); Check(await first.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
        using var art = await first.ResolveArtworkAsync("06EC10", 0, "detail"); root = art!.PackageRoot; slot = Path.GetDirectoryName(root)!;
    }
    string interrupted = Path.Combine(slot, ".download-" + Guid.NewGuid().ToString("N")), superseded = Path.Combine(slot, new string('a', 64));
    Directory.CreateDirectory(interrupted); Directory.CreateDirectory(superseded);
    using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    Check(!Directory.Exists(interrupted) && !Directory.Exists(superseded) && Directory.Exists(root), "Startup sweep removed the wrong directories");
}
static string HashFile(string path, out long length)
{
    var method = typeof(ResourceCatalog).Assembly.GetType("HeyMelodyWindows.Resources.ResourceFiles")!.GetMethod("Sha256File", BindingFlags.NonPublic | BindingFlags.Static)!;
    var arguments = new object?[] { path, 0L };
    string hash = (string)method.Invoke(null, arguments)!; length = (long)arguments[1]!; return hash;
}
static byte[]? OriginalBytes(ResourceCatalog catalog) => typeof(ResourceCatalog).GetProperty("OriginalBytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog) as byte[];
static string[] Residue(Fixture fixture)
{
    string root = Path.Combine(fixture.Path, "online/heymelody/artwork");
    return Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToArray() : [];
}
static string Slot(Fixture fixture, string productId, int color, bool model = false) => Path.Combine(fixture.Path, "online/heymelody/artwork", productId + "_" + color + "_" + (model ? "14" : "control"));
static async Task StreamingHash()
{
    string directory = Path.Combine(System.IO.Path.GetFullPath("artifacts/resource-tests"), "hash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try
    {
        foreach (int size in new[] { 0, 1, 4095, 65535, 65536, 65537, 131072, 200001 })
        {
            byte[] bytes = new byte[size]; new Random(size).NextBytes(bytes);
            string path = Path.Combine(directory, size + ".bin"); await File.WriteAllBytesAsync(path, bytes);
            Eq(Convert.ToHexStringLower(SHA256.HashData(bytes)), HashFile(path, out long length)); Eq((long)size, length);
        }
        // A concurrent reader is allowed (as for ReadAllBytes); a missing file keeps its IOException category.
        string shared = Path.Combine(directory, "200001.bin");
        using (File.Open(shared, FileMode.Open, FileAccess.Read, FileShare.Read)) Eq(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(shared))), HashFile(shared, out _));
        try { HashFile(Path.Combine(directory, "missing.bin"), out _); throw new InvalidOperationException("Missing file was hashed"); }
        catch (TargetInvocationException error) when (error.InnerException is IOException) { }
    }
    finally { Directory.Delete(directory, true); }
}
static async Task OversizedDownload()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    string slot = Slot(fixture, "06EC10", 0);
    // The cache must stay in the process-wide limit even when the server never declares a length.
    foreach (long length in new[] { ResourceLimits.ArchiveBytes + 1L, 3L * ResourceLimits.ArchiveBytes })
    {
        fixture.Handler.StreamedArtwork = length; fixture.Handler.Served = 0;
        Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Oversized body was accepted");
        Check(fixture.Handler.Served <= ResourceLimits.ArchiveBytes + 65536L, "Oversized body was read past the limit: " + fixture.Handler.Served);
        Check(coordinator.LastError is not null && !Directory.Exists(slot) && Residue(fixture).Length == 0, "Oversized body left residue: " + string.Join(", ", Residue(fixture)));
        int requests = fixture.Handler.Requests;
        Check(!await coordinator.RefreshArtworkAsync("06EC10", 0) && fixture.Handler.Requests == requests, "Oversized body was not throttled like other failures");
    }
    // A declared length is rejected before any byte is written.
    fixture.Handler.StreamedArtwork = 0; fixture.Handler.DeclaredArtworkLength = ResourceLimits.ArchiveBytes + 1L; fixture.Handler.Served = 0;
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true) && fixture.Handler.Served == 0 && !Directory.Exists(slot), "Declared oversize was not rejected up front");
    fixture.Handler.StreamedArtwork = null; fixture.Handler.DeclaredArtworkLength = null; fixture.Handler.Artwork = ArtZip(1);
    Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Valid art failed after oversized responses");
}
static async Task FailedDownloadResidue()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    string slot = Slot(fixture, "06EC10", 0);
    fixture.Handler.Artwork = ArtZip(1); fixture.Handler.WrongHash = true;
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Wrong digest was accepted");
    Check(!Directory.Exists(slot) && Residue(fixture).Length == 0, "Digest mismatch left residue: " + string.Join(", ", Residue(fixture)));
    fixture.Handler.WrongHash = false; fixture.Handler.Artwork = ArtZip(2, "../escaped.png");
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Traversal archive was accepted");
    Check(Residue(fixture).All(entry => !entry.Contains(".download-", StringComparison.Ordinal)), "Rejected archive left a scratch or extraction directory: " + string.Join(", ", Residue(fixture)));
    // A failed replacement leaves exactly the published package and its pointer.
    fixture.Handler.Artwork = ArtZip(3); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Valid art failed");
    string[] published = Directory.EnumerateFileSystemEntries(slot).Order().ToArray(); Eq(2, published.Length);
    fixture.Handler.Artwork = ArtZip(4); fixture.Handler.WrongHash = true;
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Wrong digest replaced art");
    Check(Directory.EnumerateFileSystemEntries(slot).Order().SequenceEqual(published), "Failed replacement changed the slot: " + string.Join(", ", Directory.EnumerateFileSystemEntries(slot)));
    // Cancellation in the middle of a body.
    fixture.Handler.WrongHash = false;
    using var fresh = new Fixture(); using var other = fresh.Coordinator(); await other.InitializeAsync();
    fresh.Handler.StreamedArtwork = 8L << 20; fresh.Handler.StallAfter = 200_000; fresh.Handler.Stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cancel = new CancellationTokenSource();
    var running = other.RefreshArtworkAsync("06EC10", 0, force: true, token: cancel.Token);
    await fresh.Handler.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(Residue(fresh).Any(entry => entry.EndsWith("archive.zip", StringComparison.Ordinal)), "The archive was not streamed to a scratch file");
    cancel.Cancel();
    try { await running; throw new InvalidOperationException("Cancelled refresh completed"); } catch (OperationCanceledException) { }
    Check(!Directory.Exists(Slot(fresh, "06EC10", 0)) && Residue(fresh).Length == 0, "Cancellation left residue: " + string.Join(", ", Residue(fresh)));
    fresh.Handler.StreamedArtwork = null; fresh.Handler.StallAfter = -1; fresh.Handler.Artwork = ArtZip(1);
    Check(await other.RefreshArtworkAsync("06EC10", 0), "Cancellation throttled the next check");
}
static async Task TamperedLease()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    fixture.Handler.Artwork = ArtZip(1); Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true), "Initial art failed");
    Check(Residue(fixture).All(entry => !entry.Contains(".download-", StringComparison.Ordinal) && !entry.EndsWith("archive.zip", StringComparison.Ordinal)), "Published art kept scratch files: " + string.Join(", ", Residue(fixture)));
    string path, online = Path.Combine("online", "heymelody");
    using (var first = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail")) { Check(first is not null && first.FullPath.Contains(online, StringComparison.Ordinal), "Online art was not served"); path = first!.FullPath; }
    byte[] original = File.ReadAllBytes(path), tampered = (byte[])original.Clone(); tampered[^1] ^= 1; File.WriteAllBytes(path, tampered);
    using (var fallback = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail")) Check(fallback is not null && !fallback.FullPath.Contains(online, StringComparison.Ordinal), "A tampered file was leased");
    int requests = fixture.Handler.Requests;
    Check(await coordinator.RefreshArtworkAsync("06EC10", 0, force: true) && fixture.Handler.Requests > requests, "A forced check did not re-download the tampered package");
    using var repaired = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
    Check(repaired is not null && repaired.FullPath.Contains(online, StringComparison.Ordinal) && File.ReadAllBytes(repaired.FullPath).SequenceEqual(original), "Re-download did not restore the original bytes");
    Eq(Convert.ToHexStringLower(SHA256.HashData(original)), repaired!.Sha256); Eq(original.LongLength, repaired.Length);
}
static async Task DamagedMembers()
{
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6yAAAAABJRU5ErkJggg==");
    byte[] animation = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-frame.webp"));
    byte[] truncatedWebp = animation[..^5]; BinaryPrimitives.WriteUInt32LittleEndian(truncatedWebp.AsSpan(4, 4), (uint)truncatedWebp.Length - 8); // RIFF size agrees; the last chunk overruns it.
    byte[] valid = GlbBytes();
    var bad = new (string Name, string Member, byte[] Bytes)[]
    {
        ("GLB length differs from its header", "m.glb", GlbBytes(total: valid.Length + 4)),
        ("GLB JSON chunk overruns the file", "m.glb", GlbBytes(chunk: 5000)),
        ("GLB JSON chunk length is absurd", "m.glb", GlbBytes(chunk: int.MinValue)),
        ("GLB shorter than its header", "m.glb", valid[..19]),
        ("GLB without JSON", "m.glb", GlbBytes(json: "")),
        ("GLB with a wrong magic", "m.glb", [.. "gltf"u8.ToArray(), .. valid[4..]]),
        ("WebP shorter than a RIFF header", "m.webp", animation[..11]),
        ("WebP chunk overruns the file", "m.webp", truncatedWebp),
        ("vfxms shorter than its header", "m.vfxms", new byte[39]),
        ("vfxms slice leaves the file", "m.vfxms", Vfxms(valid, trail: 0)[..^1]),
        ("vfxms slice is not a GLB", "m.vfxms", Vfxms(GlbBytes(total: valid.Length + 4))),
    };
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var refreshExceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    void CaptureRefreshException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
    {
        refreshExceptions.Enqueue(args.Exception);
        while (refreshExceptions.Count > 16) refreshExceptions.TryDequeue(out _);
    }
    foreach (var (name, member, bytes) in bad)
    {
        string key = member[(member.IndexOf('.') + 1)..];
        string config = "{\"detailImageRes\":{\"path\":\"res/image/detail.png\"},\"" + (key == "webp" ? "modelWebp" : "model3D") + "\":{\"path\":\"res/raw/" + member + "\"}}";
        fixture.Handler.Artwork = PackageZip(config, ("res/image/detail.png", png), ("res/raw/" + member, bytes));
        refreshExceptions.Clear();
        bool refreshed;
        AppDomain.CurrentDomain.FirstChanceException += CaptureRefreshException;
        try { refreshed = await coordinator.RefreshArtworkAsync("06EC10", 0, force: true); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= CaptureRefreshException; }
        if (!refreshed)
        {
            // Refresh returns only a bool; preserve bounded evidence of errors caught inside it.
            var diagnostic = new StringBuilder($"REFRESH DIAGNOSTIC: {name}; LastError={coordinator.LastError ?? "<null>"}; Revision={coordinator.Revision}\n");
            foreach (var error in refreshExceptions)
            {
                string message = $"{error.GetType().FullName} HResult=0x{error.HResult:X8}: {error.Message}";
                diagnostic.AppendLine(message[..Math.Min(message.Length, 220)]);
                var frames = (error.StackTrace ?? "").Split('\n');
                var relevant = frames.Where(frame => frame.Contains("HeyMelody", StringComparison.Ordinal)).Take(3).ToArray();
                string stack = string.Join(Environment.NewLine, relevant.Length > 0 ? relevant : frames.Take(2));
                diagnostic.AppendLine(stack[..Math.Min(stack.Length, 320)]);
            }
            string output = diagnostic.ToString();
            Console.WriteLine(output[..Math.Min(output.Length, 5998)]);
        }
        Check(refreshed, name + " failed the whole package");
        using var lease = await coordinator.ResolveArtworkAsync("06EC10", 0, "detail");
        var roles = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(lease!.PackageRoot, "artwork.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Select(entry => entry.Role).ToArray();
        Check(roles.SequenceEqual(["detail"]), name + " was indexed: " + string.Join(",", roles));
    }
    // A right GLB with trailing binary data is indexed; nothing usable still fails the refresh and keeps the previous package.
    fixture.Handler.Artwork = PackageZip("{\"model3D\":{\"path\":\"res/raw/m.glb\"}}", ("res/raw/m.glb", GlbBytes(extra: 70000)));
    Check(await coordinator.RefreshArtworkAsync("06EC10", 0, model: true, force: true), "A valid GLB was rejected");
    using var model = await coordinator.ResolveArtworkAsync("06EC10", 0, "hero"); Check(model is { Kind: "model" }, "A valid GLB was not indexed");
    fixture.Handler.Artwork = PackageZip("{\"model3D\":{\"path\":\"res/raw/m.glb\"}}", ("res/raw/m.glb", GlbBytes(chunk: 5000)));
    Check(!await coordinator.RefreshArtworkAsync("06EC10", 0, model: true, force: true), "A package without usable art replaced the previous one");
    Check(File.Exists(model!.FullPath), "A rejected package removed the previous one");
}
static async Task VfxmsSlice()
{
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(); await coordinator.InitializeAsync();
    var verified = coordinator.Bundled.FindAsset("068C10", 1, "model")!; byte[] glb = File.ReadAllBytes(coordinator.Bundled.FullPath(verified));
    foreach (var (lead, trail) in new[] { (0, 0), (5, 17), (65536, 3) })
    {
        fixture.Handler.Artwork = PackageZip("{\"model3D\":{\"path\":\"res/raw/model.vfxms\"}}", ("res/raw/model.vfxms", Vfxms(glb, lead, trail)));
        Check(await coordinator.RefreshArtworkAsync("068C10", 1, model: true, force: true), "A valid vfxms container was rejected");
        using var lease = await coordinator.ResolveArtworkAsync("068C10", 1, "model");
        Check(lease is { Kind: "model" } && Path.GetFileName(lease.FullPath) == "model.glb", "The vfxms model was not published as model.glb");
        Check(File.ReadAllBytes(lease!.FullPath).SequenceEqual(glb), "The vfxms slice differs from the embedded GLB (lead " + lead + ", trail " + trail + ")");
        Eq(verified.Sha256, lease.Sha256); Eq(glb.LongLength, lease.Length);
    }
}
static async Task OlderResponseRepair()
{
    using var fixture = new Fixture();
    byte[] current = Config(117006010);
    async Task Damage(ResourceCoordinator coordinator)
    {
        string path = Path.Combine(fixture.Path, "online/heymelody/config", coordinator.Catalog.Sha256, "config.cache");
        byte[] broken = await File.ReadAllBytesAsync(path); broken[^1] ^= 1; await File.WriteAllBytesAsync(path, broken);
        fixture.Handler.Configuration = Config(117006006); fixture.Handler.FileVersion = "older-" + Guid.NewGuid().ToString("N");
        long revision = coordinator.Revision; Check(!await coordinator.RefreshAsync(true), "An older response was adopted"); Eq(revision, coordinator.Revision); Eq(117006010, coordinator.Catalog.ContentVersion);
        Check(ResourceCacheCodec.Decrypt(await File.ReadAllBytesAsync(path), Convert.FromBase64String(coordinator.Preferences.CacheEncryptionKey)).SequenceEqual(current), "The damaged cache was not repaired with the current snapshot");
        Check(coordinator.LastError is null, "Repair was reported as a failure");
    }
    using (var coordinator = fixture.Coordinator())
    {
        await coordinator.InitializeAsync(); fixture.Handler.Configuration = current; Check(await coordinator.RefreshAsync(true), "Initial update failed");
        await Damage(coordinator);
    }
    // After a restart the retained bytes come from the decrypted cache.
    using var restarted = fixture.Coordinator(); await restarted.InitializeAsync(); Eq(117006010, restarted.Catalog.ContentVersion);
    await Damage(restarted);
}
static async Task SharedBundledCatalog()
{
    var store = new BundledResourceStore(); var bundled = store.GetCatalog();
    Check(ReferenceEquals(bundled, store.GetCatalog()), "The bundled catalog was parsed again");
    Check(OriginalBytes(bundled) is null, "The bundled snapshot retained its bytes");
    byte[] config = Config(5);
    Check(OriginalBytes(ResourceCatalog.Parse(config, false)) is null, "An offline parse retained its bytes");
    Check(OriginalBytes(ResourceCatalog.Parse(config, true))!.SequenceEqual(config), "An online parse lost its bytes");
    using var fixture = new Fixture(); using var coordinator = fixture.Coordinator();
    Check(ReferenceEquals(coordinator.Catalog, coordinator.Bundled.GetCatalog()), "The coordinator did not start from the shared bundled catalog");
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010); Check(await coordinator.RefreshAsync(true), "Online update failed");
    Check(OriginalBytes(coordinator.Catalog)!.SequenceEqual(fixture.Handler.Configuration), "The online snapshot did not retain its bytes");
    using var restarted = fixture.Coordinator(); await restarted.InitializeAsync();
    Check(restarted.Catalog.IsOnline && OriginalBytes(restarted.Catalog)!.SequenceEqual(fixture.Handler.Configuration), "A cached online snapshot did not retain its bytes");
}
static async Task WaitFor(Func<bool> condition, string reason, int milliseconds = 5000)
{
    for (int waited = 0; !condition(); waited += 10)
    {
        if (waited >= milliseconds) throw new InvalidOperationException("Timed out: " + reason);
        await Task.Delay(10);
    }
}
static Task Settle() => Task.Delay(300);
static async Task AutoRefreshOffline()
{
    using var fixture = new Fixture(); var time = new ManualTime(); using var coordinator = fixture.Coordinator(time);
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010);
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
    coordinator.StartAutoRefresh(); await Settle();
    Eq(0, fixture.Handler.Requests); Eq(0, time.Pending().Length);
    time.Advance(TimeSpan.FromHours(3)); await Settle(); Eq(0, fixture.Handler.Requests);
    // The previous pass began hours ago, so the 30-second floor no longer applies.
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = true });
    var due = time.GetUtcNow().AddMinutes(10);
    await WaitFor(() => fixture.Handler.Requests > 0 && time.Pending().Contains(due), "Turning online resources on did not check at once");
    Eq(2, fixture.Handler.Requests);
}
static async Task AutoRefreshSchedule()
{
    using var fixture = new Fixture(); var time = new ManualTime(); using var coordinator = fixture.Coordinator(time);
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010);
    var start = time.GetUtcNow(); coordinator.StartAutoRefresh();
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(10)), "The first check did not schedule the next one 10 minutes out");
    int requests = fixture.Handler.Requests; Check(requests > 0, "No initial check");
    time.Advance(TimeSpan.FromMinutes(9)); await Settle(); Eq(requests, fixture.Handler.Requests);
    time.Advance(TimeSpan.FromMinutes(1));
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(20)), "The 10-minute re-check did not run");
    Eq(requests + 1, fixture.Handler.Requests);
    fixture.Handler.Failure = new HttpRequestException("down"); requests = fixture.Handler.Requests;
    time.Advance(TimeSpan.FromMinutes(10));
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(20).AddSeconds(30)), "A failure was not retried after 30 seconds");
    Eq(requests + 1, fixture.Handler.Requests);
    time.Advance(TimeSpan.FromSeconds(29)); await Settle(); Eq(requests + 1, fixture.Handler.Requests);
    time.Advance(TimeSpan.FromSeconds(1));
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(21)), "The retry did not run");
    Eq(requests + 2, fixture.Handler.Requests); Check(coordinator.LastError is not null, "The failure was not reported");
    fixture.Handler.Failure = null; time.Advance(TimeSpan.FromSeconds(30));
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(31)), "A recovered check did not return to the 10-minute cadence");
    Check(coordinator.LastError is null, "Recovery left the error");
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
    await WaitFor(() => time.Pending().Length == 0, "Turning online resources off left a timer");
    requests = fixture.Handler.Requests; time.Advance(TimeSpan.FromHours(2)); await Settle(); Eq(requests, fixture.Handler.Requests);
}
static async Task AutoRefreshUnexpectedFailure()
{
    using var fixture = new Fixture(); var time = new ManualTime(); using var coordinator = fixture.Coordinator(time);
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010);
    // Not one of the failures RefreshAsync handles: it escapes the pass, as an invalid request URI from HttpClient would.
    fixture.Handler.Failure = new InvalidOperationException("unexpected");
    var start = time.GetUtcNow(); coordinator.StartAutoRefresh();
    await WaitFor(() => time.Pending().Contains(start.AddSeconds(30)), "An unexpected failure stopped the loop instead of waiting for the floor");
    int requests = fixture.Handler.Requests; Check(requests > 0 && coordinator.LastError is not null, "The failed pass made no request or was not reported");
    time.Advance(TimeSpan.FromSeconds(29)); await Settle(); Eq(requests, fixture.Handler.Requests);
    fixture.Handler.Failure = null; time.Advance(TimeSpan.FromSeconds(1));
    await WaitFor(() => time.Pending().Contains(start.AddSeconds(30).AddMinutes(10)), "The loop did not retry after the 30-second floor");
    Check(fixture.Handler.Requests > requests && coordinator.LastError is null, "The retry did not recover");
}
static async Task AutoRefreshArtwork()
{
    using var fixture = new Fixture(); var time = new ManualTime(); using var coordinator = fixture.Coordinator(time);
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010); fixture.Handler.Artwork = ArtZip(1);
    var start = time.GetUtcNow(); coordinator.StartAutoRefresh();
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(10)), "The first check did not run");
    time.Advance(TimeSpan.FromMinutes(4));
    Check(await coordinator.RefreshArtworkAsync("06EC10", 0), "Artwork was not downloaded");
    await Settle(); // the loop re-arms its wait after the new target's signal
    time.Advance(TimeSpan.FromMinutes(6));
    // Configuration is due now; the artwork slot added at +4 minutes is due at +14.
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(14)), "The added artwork target did not set the next wake");
    int requests = fixture.Handler.Requests; Check(!time.Pending().Contains(start.AddMinutes(20)), "Only the earliest due time should be waited on");
    time.Advance(TimeSpan.FromMinutes(4));
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(20)), "The artwork check did not run");
    Eq(requests + 1, fixture.Handler.Requests);
}
static async Task AutoRefreshFloor()
{
    using var fixture = new Fixture(); var time = new ManualTime(); using var coordinator = fixture.Coordinator(time);
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010);
    var start = time.GetUtcNow(); coordinator.StartAutoRefresh();
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(10)), "The first check did not run");
    time.Advance(TimeSpan.FromSeconds(1)); int requests = fixture.Handler.Requests;
    // The change clears every throttle, so a check is due now, but the previous pass began only a second ago.
    await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { HeyMelodyKey = "test-key" });
    await WaitFor(() => time.Pending().Contains(start.AddSeconds(30)), "The pass floor was not applied");
    Check(time.Pending().Length == 1, "The superseded timer was left armed"); await Settle(); Eq(requests, fixture.Handler.Requests);
    time.Advance(TimeSpan.FromSeconds(29));
    await WaitFor(() => time.Pending().Contains(start.AddSeconds(30).AddMinutes(10)), "The floored pass did not run");
    Eq(requests + 1, fixture.Handler.Requests);
}
static async Task AutoRefreshReschedule()
{
    using var fixture = new Fixture(); var time = new ManualTime(); using var coordinator = fixture.Coordinator(time);
    await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010);
    var start = time.GetUtcNow(); coordinator.StartAutoRefresh();
    await WaitFor(() => time.Pending().Contains(start.AddMinutes(10)), "The first check did not run");
    int requests = fixture.Handler.Requests;
    // A 30-minute sleep: the wall clock moved on, but the armed timer never fired.
    time.Skip(TimeSpan.FromMinutes(30)); await Settle(); Eq(requests, fixture.Handler.Requests);
    coordinator.Reschedule();
    await WaitFor(() => fixture.Handler.Requests > requests && time.Pending().Contains(start.AddMinutes(40)), "Rescheduling did not run the overdue check");
    Eq(1, time.Pending().Length);
    // Nothing is due now: the wait is re-armed for the same deadline and no request is made.
    requests = fixture.Handler.Requests; coordinator.Reschedule(); await Settle();
    Eq(requests, fixture.Handler.Requests); Check(time.Pending().SequenceEqual([start.AddMinutes(40)]), "Rescheduling moved the next check");
}
static async Task AutoRefreshDispose()
{
    foreach (bool online in new[] { true, false })
    {
        using var fixture = new Fixture(); var time = new ManualTime(); var coordinator = fixture.Coordinator(time);
        await coordinator.InitializeAsync(); fixture.Handler.Configuration = Config(117006010);
        if (!online) await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
        var start = time.GetUtcNow(); coordinator.StartAutoRefresh();
        if (online) await WaitFor(() => time.Pending().Contains(start.AddMinutes(10)), "The loop did not go to sleep"); else await Settle();
        var loop = (Task)typeof(ResourceCoordinator).GetField("_autoRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coordinator)!;
        coordinator.Dispose();
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Eq(0, time.Pending().Length);
    }
}
static byte[] GlbBytes(string json = "{\"asset\":{\"version\":\"2.0\"}} ", int? total = null, int? chunk = null, int extra = 0)
{
    byte[] body = Encoding.UTF8.GetBytes(json), glb = new byte[20 + body.Length + extra];
    "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)(total ?? glb.Length)); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)(chunk ?? body.Length));
    "JSON"u8.CopyTo(glb.AsSpan(16)); body.CopyTo(glb, 20); for (int index = 20 + body.Length; index < glb.Length; index++) glb[index] = (byte)(index * 31);
    return glb;
}
static byte[] Vfxms(byte[] glb, int lead = 5, int trail = 17)
{
    byte[] container = new byte[40 + lead + glb.Length + trail];
    BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(8, 4), 40 + lead); BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(12, 4), glb.Length);
    glb.CopyTo(container, 40 + lead); container.AsSpan(40 + lead + glb.Length).Fill(0xAB);
    return container;
}
static byte[] PackageZip(string config, params (string Name, byte[] Bytes)[] members)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write(config);
        foreach (var (name, bytes) in members) using (var stream = archive.CreateEntry(name).Open()) stream.Write(bytes);
    }
    return memory.ToArray();
}
static byte[] LottieZip()
{
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6yAAAAABJRU5ErkJggg==");
    static string Animation(string image) => "{\"v\":\"5.7.4\",\"fr\":30,\"ip\":0,\"op\":60,\"w\":64,\"h\":64,\"assets\":[" + image + "],\"layers\":[]}";
    var members = new Dictionary<string, byte[]>
    {
        ["config.json"] = Encoding.UTF8.GetBytes("{\"detailImageRes\":{\"path\":\"res/image/detail.png\"},\"lottieResList\":[{\"path\":\"res/lottie/missing.json\"},"
            + "{\"path\":\"res/lottie/main\",\"imagePath\":\"res/lottie/main/images\",\"type\":1},{\"path\":\"res/lottie/second.json\"}],"
            + "\"leftLottieResList\":[{\"path\":\"res/lottie/left.json\"}],\"discoveryStates\":{\"connectedState\":{\"bgVideoRes\":{\"path\":\"res/video/boot\"}}}}"),
        ["res/image/detail.png"] = png,
        ["res/lottie/main/data.json"] = Encoding.UTF8.GetBytes(Animation("{\"id\":\"img\",\"w\":1,\"h\":1,\"u\":\"\",\"p\":\"img_0.png\",\"e\":0}")),
        ["res/lottie/main/images/img_0.png"] = png,
        ["res/lottie/second.json"] = Encoding.UTF8.GetBytes(Animation("")),
        ["res/lottie/left.json"] = Encoding.UTF8.GetBytes(Animation("")),
    };
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        foreach (var (name, bytes) in members) using (var stream = archive.CreateEntry(name).Open()) stream.Write(bytes);
    return memory.ToArray();
}
static byte[] ModelZip(byte[] glb)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write("{\"model3D\":{\"path\":\"res/raw/model.glb\"}}");
        using (var model = archive.CreateEntry("res/raw/model.glb").Open()) model.Write(glb);
    }
    return memory.ToArray();
}
static byte[] ModelWebpZip(byte[] webp)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write("{\"modelWebp\":{\"path\":\"res/raw/detail_model.webp\"}}");
        using (var model = archive.CreateEntry("res/raw/detail_model.webp").Open()) model.Write(webp);
    }
    return memory.ToArray();
}
static byte[] ArtZip(int revision, string? unsafeName = null)
{
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6yAAAAABJRU5ErkJggg==");
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("config.json").Open())) writer.Write("{\"revision\":" + revision + ",\"detailImageRes\":{\"path\":\"res/image/detail.png\"}}");
        using (var image = archive.CreateEntry("res/image/detail.png").Open()) image.Write(png);
        if (unsafeName is not null) using (var image = archive.CreateEntry(unsafeName).Open()) image.Write(png);
    }
    return memory.ToArray();
}

sealed class Fixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath("artifacts/resource-tests"), Guid.NewGuid().ToString("N"));
    public FakeVendorHandler Handler { get; } = new();
    public ResourceCoordinator Coordinator(TimeProvider? time = null) => new(Path, new HttpClient(Handler, false)) { Time = time ?? TimeProvider.System };
    public void Dispose()
    {
        string allowed = System.IO.Path.GetFullPath("artifacts/resource-tests") + System.IO.Path.DirectorySeparatorChar;
        if (!System.IO.Path.GetFullPath(Path).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup");
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
sealed class FakeVendorHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public byte[] Configuration { get; set; } = Encoding.UTF8.GetBytes("{\"versionCode\":117006010,\"whiteList\":[]}");
    public string FileVersion { get; set; } = "initial";
    public byte[] Artwork { get; set; } = [];
    public byte[]? ModelArtwork { get; set; }
    public bool WrongHash { get; set; }
    public string? LastVerifiedKey { get; private set; }
    public TaskCompletionSource? Gate { get; set; }
    public Exception? Failure { get; set; }
    /// <summary>Serves artwork as a lazy stream of this many bytes with no Content-Length.</summary>
    public long? StreamedArtwork { get; set; }
    public long? DeclaredArtworkLength { get; set; }
    public long StallAfter { get; set; } = -1;
    public TaskCompletionSource? Stalled { get; set; }
    public long Served { get => Interlocked.Read(ref _served); set => Interlocked.Exchange(ref _served, value); }
    private long _served;
    public void Add(int count) => Interlocked.Add(ref _served, count);
    public int Pending => _pending;
    private int _pending;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++; byte[] bytes;
        if (Gate is { Task.IsCompleted: false } gate)
        {
            Interlocked.Increment(ref _pending);
            try { await gate.Task.WaitAsync(token); } finally { Interlocked.Decrement(ref _pending); }
        }
        if (Failure is { } failure) throw failure;
        if (request.Method == HttpMethod.Post)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(token); string signed = request.Headers.GetValues("sign").Single();
            if (signed == VendorResourceClient.Sign("test-key", body)) LastVerifiedKey = "test-key";
            CheckHeader(request, "appid", "earphone"); CheckHeader(request, "sv", "v1");
            if (request.RequestUri!.AbsolutePath.EndsWith("latestWhiteList"))
                bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { version = FileVersion, size = Configuration.Length.ToString(), downloadUrl = "https://iot-earbuds-cn.heytapimage.com/config" } });
            else
            {
                bool model = request.RequestUri.AbsolutePath.EndsWith("/fetch");
                string hash = WrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(model ? ModelArtwork ?? Artwork : Artwork));
                if (model) bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { sha256 = hash, resourceUrl = "https://iot-earbuds-cn.heytapimage.com/model-art", fileName = "model.zip" } });
                else bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { productId = "06EC10", sha256 = hash, cartoonUrl = "https://iot-earbuds-cn.heytapimage.com/art" } });
            }
        }
        else bytes = request.RequestUri!.AbsolutePath == "/config" ? Configuration : request.RequestUri.AbsolutePath == "/model-art" ? ModelArtwork ?? Artwork : Artwork;
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath != "/config" && (StreamedArtwork is not null || DeclaredArtworkLength is not null))
        {
            var content = new StreamContent(new PatternStream(StreamedArtwork ?? 0, StallAfter, Stalled, this));
            if (DeclaredArtworkLength is long declared) content.Headers.ContentLength = declared;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
    }
    private static void CheckHeader(HttpRequestMessage request, string name, string value)
    { if (request.Headers.GetValues(name).Single() != value) throw new InvalidOperationException("Wrong vendor header"); }
}
sealed class PatternStream(long length, long stallAfter, TaskCompletionSource? stalled, FakeVendorHandler served) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (stallAfter >= 0 && _position >= stallAfter) { stalled?.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        int count = (int)Math.Min(buffer.Length, length - _position);
        if (stallAfter >= 0 && _position < stallAfter) count = (int)Math.Min(count, stallAfter - _position);
        buffer.Span[..count].Fill(0x5A); _position += count; served.Add(count);
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
/// <summary>Manual clock: timers fire only from <see cref="Advance"/>; <see cref="Pending"/> lists the due times still armed.</summary>
sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public DateTimeOffset[] Pending() { lock (_gate) return [.. _timers.Where(timer => timer.Armed).Select(timer => timer.Due)]; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period); return timer;
    }
    /// <summary>Moves the clock without firing anything, like a machine asleep while a long timer is armed.</summary>
    public void Skip(TimeSpan by) { lock (_gate) _now += by; }
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate) { _now += by; due = [.. _timers.Where(timer => timer.Armed && timer.Due <= _now)]; foreach (var timer in due) timer.Armed = false; }
        foreach (var timer in due) timer.Fire();
    }
    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due; public bool Armed;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) { Armed = dueTime != Timeout.InfiniteTimeSpan; Due = owner._now + (Armed ? dueTime : TimeSpan.Zero); }
            return true;
        }
        public void Dispose() { lock (owner._gate) { Armed = false; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
