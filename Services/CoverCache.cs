using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace LuaShareX.Services;

/// <summary>
/// Caches Steam game header images on disk so each is downloaded once,
/// then loaded locally/offline. Dedups concurrent downloads and remembers
/// appids with no cover (so they stop retrying).
/// </summary>
public class CoverCache
{
    private static readonly string CoversDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaShareX", "covers");

    // Reject only truncated/empty bodies. Some legit covers are tiny and highly
    // compressible, so validity is decided by JPEG magic bytes instead (see IsJpeg).
    private const int MinValidBytes = 512;

    // Steam's grey "Header Capsule" placeholder. The predictable CDN URL
    // (cdn.../steam/apps/<id>/header.jpg) serves this static image for apps that
    // have no CDN header yet. It's a *valid* JPEG, so the magic-byte check passes
    // and it would otherwise be cached as the cover. Fingerprint it exactly
    // (byte length + SHA-256) and reject it.
    private const int PlaceholderLength = 9816;
    private const string PlaceholderSha256 =
        "732ec27f2af650fe079f1c83b0bb0c712a322dc175f383504176724675ad2700";

    private static bool IsHeaderCapsulePlaceholder(byte[] b)
    {
        if (b.Length != PlaceholderLength) return false;
        var hash = Convert.ToHexString(SHA256.HashData(b));
        return hash.Equals(PlaceholderSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static string RemoteUrl(uint appId) =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ConcurrentDictionary<uint, Task<string?>> _inFlight = new();
    private readonly ConcurrentDictionary<uint, byte> _noCover = new();

    private static string PathFor(uint appId) => Path.Combine(CoversDir, $"{appId}.jpg");

    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    public string? GetLocalPath(uint appId)
    {
        string p = PathFor(appId);
        if (!File.Exists(p)) return null;

        try
        {
            if (new FileInfo(p).Length == PlaceholderLength &&
                IsHeaderCapsulePlaceholder(File.ReadAllBytes(p)))
            {
                File.Delete(p);
                return null;
            }
        }
        catch { }

        return p;
    }

    public bool IsKnownMissing(uint appId) => _noCover.ContainsKey(appId);

    public void MarkMissing(uint appId) => _noCover[appId] = 0;

    public Task<string?> EnsureAsync(uint appId, CancellationToken ct = default)
        => EnsureAsync(appId, RemoteUrl(appId), ct);

    public Task<string?> EnsureAsync(uint appId, string remoteUrl, CancellationToken ct = default)
    {
        string path = PathFor(appId);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        if (string.IsNullOrWhiteSpace(remoteUrl)) return Task.FromResult<string?>(null);

        return _inFlight.GetOrAdd(appId, _ => DownloadAsync(appId, path, remoteUrl, ct));
    }

    private async Task<string?> DownloadAsync(uint appId, string path, string remoteUrl, CancellationToken ct)
    {
        try
        {
            byte[] bytes = await _http.GetByteArrayAsync(remoteUrl, ct);
            if (bytes.Length < MinValidBytes || !IsJpeg(bytes)) return null;
            if (IsHeaderCapsulePlaceholder(bytes)) return null;

            await _ioGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(CoversDir);
                await File.WriteAllBytesAsync(path, bytes, ct);
            }
            finally
            {
                _ioGate.Release();
            }
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(appId, out _);
        }
    }
}
