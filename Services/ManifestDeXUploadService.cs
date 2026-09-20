using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LuaShareX.Services;

/// <summary>Anonymous staging client for ManifestDeX lua uploads.
/// Uploads an export zip, returns a bearer token the member confirms on the website.
/// Never requires auth; the website attributes the upload at confirm time.</summary>
public class ManifestDeXUploadService
{
    public const string ApiBase = "https://api.manifestdex.com";
    public const string FrontendBase = "https://manifestdex.com";
    private const long MaxZipBytes = 100L * 1024 * 1024;

    private readonly HttpClient _http;

    public ManifestDeXUploadService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LuaShareX", "1.2.5"));
    }

    public static string BuildConfirmUrl(string token) =>
        $"{FrontendBase}/dashboard?tab=share&luasharexupload={token}";

    public async Task<(bool Ok, string? Token, DateTime? ExpiresAt, string? Error)> StageZipAsync(
        string zipPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return (false, null, null, "Export file not found.");

            var length = new FileInfo(zipPath).Length;
            if (length > MaxZipBytes)
                return (false, null, null, "Exported zip is larger than 100 MB and cannot be staged.");
            if (length == 0)
                return (false, null, null, "Export file is empty.");

            using var fileStream = File.OpenRead(zipPath);
            using var progressStream = new ProgressReadStream(fileStream, progress);
            using var streamContent = new StreamContent(progressStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var multipart = new MultipartFormDataContent();
            multipart.Add(streamContent, "file", Path.GetFileName(zipPath));

            using var response = await _http.PostAsync($"{ApiBase}/api/luasharex/stage-luaupload", multipart, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var success = root.TryGetProperty("success", out var successEl) && successEl.GetBoolean();
                if (!success || !response.IsSuccessStatusCode)
                {
                    string? error = null;
                    if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                        error = errorEl.GetString();
                    return (false, null, null, string.IsNullOrWhiteSpace(error) ? $"Upload failed (HTTP {(int)response.StatusCode})." : error);
                }

                string? token = null;
                if (root.TryGetProperty("token", out var tokenEl) && tokenEl.ValueKind == JsonValueKind.String)
                    token = tokenEl.GetString();
                if (string.IsNullOrWhiteSpace(token))
                    return (false, null, null, "Upload failed: server did not return a token.");

                DateTime? expiresAt = null;
                if (root.TryGetProperty("expiresAt", out var expEl) && expEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(expEl.GetString(), out var parsed))
                    expiresAt = parsed;

                progress?.Report(1.0);
                return (true, token, expiresAt, null);
            }
            catch (JsonException)
            {
                return (false, null, null, $"Upload failed (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, null, null, "Upload cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, null, null, ex.Message);
        }
    }

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private readonly IProgress<double>? _progress;
        private long _read;

        public ProgressReadStream(Stream inner, IProgress<double>? progress)
        {
            _inner = inner;
            _length = inner.Length;
            _progress = progress;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Report()
        {
            if (_progress != null && _length > 0)
                _progress.Report(Math.Clamp((double)_read / _length, 0.0, 1.0));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) { _read += n; Report(); }
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (n > 0) { _read += n; Report(); }
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await _inner.ReadAsync(buffer, cancellationToken);
            if (n > 0) { _read += n; Report(); }
            return n;
        }
    }
}
