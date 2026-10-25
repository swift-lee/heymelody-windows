using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HeyMelodyWindows.Resources;

public sealed record ResourceArtwork
{
    public string[] ProductIds { get; init; } = [];
    public int? Color { get; init; }
    public string Kind { get; init; } = "";
    public string Role { get; init; } = "";
    public string Title { get; init; } = "";
    public string AndroidName { get; init; } = "";
    public bool Night { get; init; }
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Length { get; init; }
    public string? OriginalPath { get; init; }
    public string? OriginalSha256 { get; init; }
    // Position within an online list such as lottieResList, and its scalar vendor fields.
    public int? Order { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>Read-only installation assets; never a target of cache cleanup.</summary>
public sealed class BundledResourceStore
{
    private readonly string _directory;
    // The bundled snapshot is immutable; parsing it per call allocated megabytes for every lookup.
    private readonly Lazy<ResourceCatalog> _catalog = new(() => ResourceCatalog.Parse(ReadEmbedded("heymelody.json"), false, 117006004));
    public IReadOnlyList<ResourceArtwork> Artwork { get; }
    public BundledResourceStore(string? directory = null)
    {
        _directory = System.IO.Path.GetFullPath(directory ?? System.IO.Path.Combine(HeyMelody.Platform.ApplicationPaths.ResourceRoot, "ResourceBundles"));
        Artwork = JsonSerializer.Deserialize<ResourceArtwork[]>(ReadEmbedded("artwork.json"), ResourceJson.Options) ?? [];
    }

    public ResourceCatalog GetCatalog() => _catalog.Value;

    public string GetDefaultKey()
    {
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadEmbedded("default-keys.json"))!;
        return keys["HeyMelodyKey"];
    }

    public ResourceArtwork? FindAsset(string productId, int? color, string role)
    {
        var matches = Artwork.Where(a => a.ProductIds.Contains(productId, StringComparer.Ordinal)
            && (a.Color is null || color.HasValue && a.Color == color) && MatchesRole(a.Role, role));
        return matches.OrderByDescending(a => a.Color == color && color.HasValue).ThenBy(a => a.Night).FirstOrDefault(IsAvailable);
    }

    public ResourceArtwork? FindNamedAsset(string androidName, bool night = false) => Artwork
        .Where(a => a.AndroidName == androidName).OrderByDescending(a => a.Night == night).FirstOrDefault(IsAvailable);

    public ResourceArtwork? FindGenericAsset(string type, string role)
    {
        string category = type.StartsWith("N", StringComparison.OrdinalIgnoreCase) ? "device_neck" : type == "O1" ? "device_ows" : "device_default";
        if (role is "left" or "right")
        {
            var earpiece = Artwork.Where(a => a.ProductIds.Length == 0 && a.Kind == "bitmap" && !a.Night && a.Role == category
                && a.AndroidName.Contains("_ear_" + role + "_", StringComparison.OrdinalIgnoreCase)).FirstOrDefault(IsAvailable);
            if (earpiece is not null) return earpiece;
        }
        return Artwork.Where(a => a.ProductIds.Length == 0 && a.Kind == "bitmap" && !a.Night && a.Role == category)
            .OrderByDescending(a => a.AndroidName.Contains("detail_default_img", StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.AndroidName.Contains("left", StringComparison.OrdinalIgnoreCase) || a.AndroidName.Contains("right", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(IsAvailable);
    }

    public ResourceArtworkLease? Acquire(ResourceArtwork? artwork, long revision = 0)
    {
        if (artwork is null || !IsAvailable(artwork)) return null;
        string path = FullPath(artwork);
        return new ResourceArtworkLease(path, artwork.Kind, System.IO.Path.GetDirectoryName(path)!, artwork.Sha256, artwork.Length, revision, null);
    }

    public string FullPath(ResourceArtwork artwork) => ResourcePaths.Resolve(_directory, artwork.Path);
    private bool IsAvailable(ResourceArtwork artwork) => File.Exists(FullPath(artwork));
    internal static bool MatchesRole(string candidate, string requested) => candidate == requested || requested == "both" && candidate == "capsule" || requested == "hero" && candidate is "detail" or "model" or "model-animation";
    private static byte[] ReadEmbedded(string filename)
    {
        using var stream = typeof(BundledResourceStore).Assembly.GetManifestResourceStream("HeyMelody.Resources.Bundles." + filename)
            ?? throw new InvalidDataException("Missing bundled resource: " + filename);
        using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
    }
}

public sealed class ResourceArtworkLease : IDisposable
{
    private Action? _release;
    public string FullPath { get; }
    public string Kind { get; }
    public string PackageRoot { get; }
    public string Sha256 { get; }
    public long Length { get; }
    public long Revision { get; }
    internal ResourceArtworkLease(string path, string kind, string root, string hash, long length, long revision, Action? release)
    { FullPath = path; Kind = kind; PackageRoot = root; Sha256 = hash; Length = length; Revision = revision; _release = release; }
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal static class ResourcePaths
{
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') || relative.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Unsafe resource path.");
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource path escapes package root.");
        return full;
    }

    internal static void DeleteOwnedDirectory(string root, string target)
    {
        root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        target = System.IO.Path.GetFullPath(target);
        if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cleanup target is outside online cache.");
        if (!Directory.Exists(target)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Prepend(target))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked cache entries cannot be deleted.");
        Directory.Delete(target, true);
    }
}
