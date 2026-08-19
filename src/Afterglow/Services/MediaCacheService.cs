using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Afterglow.Core;
using Afterglow.HubClient;
using ImageMagick;

namespace Afterglow.Services;

/// <summary>Decoded media: always has a preview frame; GIFs may include animation frames.</summary>
public sealed class AnimatedMedia
{
    public required Bitmap Preview { get; init; }
    public IReadOnlyList<Bitmap> Frames { get; init; } = Array.Empty<Bitmap>();
    public IReadOnlyList<int> DelaysMs { get; init; } = Array.Empty<int>();
    public bool IsAnimated => Frames.Count > 1;

    public int DelayMs(int index)
    {
        if (DelaysMs.Count == 0) return 100;
        var d = DelaysMs[Math.Clamp(index, 0, DelaysMs.Count - 1)];
        return d < 20 ? 100 : d;
    }
}

public readonly record struct MediaCacheRequest
{
    public int MaxWidth { get; init; }
    public bool PreferAnimation { get; init; }
    public string? SourceVersion { get; init; }

    public static MediaCacheRequest Thumbnail(string? sourceVersion = null) => new()
    {
        MaxWidth = MediaCacheService.ThumbnailWidth,
        SourceVersion = sourceVersion
    };

    public static MediaCacheRequest Detail(string? sourceVersion = null) => new()
    {
        MaxWidth = MediaCacheService.DetailWidth,
        PreferAnimation = true,
        SourceVersion = sourceVersion
    };
}

/// <summary>
/// Steam-style media cache: files on disk, decoded thumbnails in RAM, re-fetch only when
/// hub cover URL / updated_at (SourceVersion) or ETag says the bytes changed.
/// </summary>
public sealed class MediaCacheService
{
    public const int ThumbnailWidth = 480;
    public const int DetailWidth = 1280;

    private readonly HubApiClient _hub;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AnimatedMedia> _decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ioGate = new(4, 4);
    private readonly string _cacheDir;
    private static readonly JsonSerializerOptions MetaJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MediaCacheService(HubApiClient hub)
    {
        _hub = hub;
        _cacheDir = AppPaths.MediaCache;
        Directory.CreateDirectory(_cacheDir);
    }

    public string? LastError { get; private set; }
    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }

    public void ResetStats()
    {
        SuccessCount = 0;
        FailureCount = 0;
        LastError = null;
    }

    public Task<Bitmap?> GetAsync(string? url, CancellationToken cancellationToken = default) =>
        GetAsync(url, MediaCacheRequest.Thumbnail(), cancellationToken);

    public async Task<Bitmap?> GetAsync(string? url, MediaCacheRequest request, CancellationToken cancellationToken = default)
    {
        var media = await GetMediaAsync(url, request with { PreferAnimation = false }, cancellationToken);
        return media?.Preview;
    }

    public Task<AnimatedMedia?> GetMediaAsync(string? url, CancellationToken cancellationToken = default) =>
        GetMediaAsync(url, MediaCacheRequest.Detail(), cancellationToken);

    public Task<AnimatedMedia?> GetMediaAsync(string? url, MediaCacheRequest request, CancellationToken cancellationToken = default) =>
        GetMediaAsyncCore(url, request, cancellationToken);

    private async Task<AnimatedMedia?> GetMediaAsyncCore(string? url, MediaCacheRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            FailureCount++;
            LastError ??= "No media URL from hub (cover_url empty). Refresh the game metadata.";
            return null;
        }

        var key = url.Trim();
        var maxWidth = request.MaxWidth <= 0 ? DetailWidth : request.MaxWidth;
        var memKey = $"{key}|{maxWidth}|{(request.PreferAnimation ? 1 : 0)}";

        if (_decoded.TryGetValue(memKey, out var hit) && (!request.PreferAnimation || hit.IsAnimated || !LooksLikeGifUrl(key)))
        {
            SuccessCount++;
            return hit;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_decoded.TryGetValue(memKey, out hit) && (!request.PreferAnimation || hit.IsAnimated || !LooksLikeGifUrl(key)))
            {
                SuccessCount++;
                return hit;
            }

            await _ioGate.WaitAsync(cancellationToken);
            try
            {
                var rawPath = CachePathFor(key);
                var pngPath = Path.ChangeExtension(rawPath, ".png");
                var thumbPath = ThumbPathFor(key, maxWidth);
                var metaPath = Path.ChangeExtension(rawPath, ".meta.json");
                var meta = ReadMeta(metaPath);

                var stale = !string.IsNullOrWhiteSpace(request.SourceVersion)
                            && !string.IsNullOrWhiteSpace(meta.SourceVersion)
                            && !string.Equals(meta.SourceVersion, request.SourceVersion, StringComparison.Ordinal);

                var hasFile = File.Exists(rawPath) || File.Exists(pngPath) || File.Exists(thumbPath);
                if (!hasFile || stale)
                {
                    var fetched = await FetchToDiskAsync(key, rawPath, meta, stale, cancellationToken);
                    if (!fetched && !hasFile)
                    {
                        FailureCount++;
                        return null;
                    }

                    if (fetched)
                    {
                        TryDelete(pngPath);
                        TryDelete(thumbPath);
                        hasFile = File.Exists(rawPath);
                    }

                    meta.SourceVersion = request.SourceVersion ?? meta.SourceVersion;
                    WriteMeta(metaPath, meta);
                }
                else if (!string.IsNullOrWhiteSpace(request.SourceVersion) && string.IsNullOrWhiteSpace(meta.SourceVersion))
                {
                    meta.SourceVersion = request.SourceVersion;
                    WriteMeta(metaPath, meta);
                }

                var isGif = LooksLikeGifUrl(key)
                            || (File.Exists(rawPath) && LooksLikeGif(rawPath))
                            || string.Equals(Path.GetExtension(rawPath), ".gif", StringComparison.OrdinalIgnoreCase);

                if (request.PreferAnimation && isGif && File.Exists(rawPath))
                {
                    var decoded = await Task.Run(() => DecodeGifBytes(rawPath, maxWidth), cancellationToken);
                    if (decoded is { Count: > 0 })
                    {
                        var gif = await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var frames = new List<Bitmap>(decoded.Count);
                            var delays = new List<int>(decoded.Count);
                            foreach (var (png, delay) in decoded)
                            {
                                frames.Add(new Bitmap(new MemoryStream(png)));
                                delays.Add(delay);
                            }
                            return new AnimatedMedia
                            {
                                Preview = frames[0],
                                Frames = frames,
                                DelaysMs = delays
                            };
                        });
                        _decoded[memKey] = gif;
                        SuccessCount++;
                        return gif;
                    }
                }

                Bitmap? bitmap = null;
                if (File.Exists(thumbPath))
                    bitmap = await LoadBitmapAsync(thumbPath, maxWidth: 0);

                if (bitmap is null && File.Exists(pngPath))
                    bitmap = await LoadBitmapAsync(pngPath, maxWidth);

                if (bitmap is null && File.Exists(rawPath))
                {
                    var needsMagick = LooksLikeAvif(rawPath)
                                      || string.Equals(Path.GetExtension(rawPath), ".avif", StringComparison.OrdinalIgnoreCase)
                                      || isGif;
                    if (!needsMagick)
                        bitmap = await LoadBitmapAsync(rawPath, maxWidth);

                    if (bitmap is null)
                    {
                        try
                        {
                            await Task.Run(() => ConvertToPng(rawPath, thumbPath, maxWidth), cancellationToken);
                            bitmap = await LoadBitmapAsync(thumbPath, maxWidth: 0);
                            if (bitmap is not null && !isGif)
                                TryDelete(rawPath);
                        }
                        catch (Exception ex)
                        {
                            LastError = $"Decode failed: {ex.Message} ({Path.GetFileName(rawPath)})";
                            TryDelete(rawPath);
                            TryDelete(pngPath);
                            TryDelete(thumbPath);
                        }
                    }
                    else if (!File.Exists(thumbPath))
                    {
                        // Keep a small PNG so the next launch skips re-decoding the original.
                        try { await Task.Run(() => ConvertToPng(rawPath, thumbPath, maxWidth), cancellationToken); }
                        catch { /* thumb is optional when Avalonia already decoded */ }
                    }
                }

                if (bitmap is null)
                {
                    FailureCount++;
                    LastError ??= $"Decode failed ({Path.GetFileName(rawPath)})";
                    return null;
                }

                var result = new AnimatedMedia { Preview = bitmap };
                _decoded[memKey] = result;
                SuccessCount++;
                return result;
            }
            finally
            {
                _ioGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FailureCount++;
            LastError = ex.Message;
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> FetchToDiskAsync(string url, string rawPath, CacheMeta meta, bool allowConditional, CancellationToken cancellationToken)
    {
        var ifNoneMatch = allowConditional && File.Exists(rawPath) ? meta.ETag : null;
        var download = await _hub.DownloadBytesDetailedAsync(url, ifNoneMatch, cancellationToken);
        if (download.NotModified)
        {
            if (!string.IsNullOrWhiteSpace(download.ETag))
                meta.ETag = download.ETag;
            return false;
        }

        if (download.Bytes is null || download.Bytes.Length < 24)
        {
            LastError = download.Error ?? $"Empty media response (HTTP {download.Status}) for {Truncate(url)}";
            return File.Exists(rawPath);
        }

        if (download.Bytes[0] is (byte)'<' or (byte)'{' or (byte)'[')
        {
            LastError = $"Media URL returned non-image payload (HTTP {download.Status}) for {Truncate(url)}";
            return File.Exists(rawPath);
        }

        await File.WriteAllBytesAsync(rawPath, download.Bytes, cancellationToken);
        meta.ETag = download.ETag;
        return true;
    }

    private static List<(byte[] Png, int DelayMs)>? DecodeGifBytes(string path, int maxWidth)
    {
        try
        {
            using var collection = new MagickImageCollection(path);
            if (collection.Count == 0) return null;
            collection.Coalesce();

            var frames = new List<(byte[] Png, int DelayMs)>(Math.Min(collection.Count, 24));
            int n = 0;
            foreach (var frame in collection)
            {
                if (n++ >= 24) break;
                if (maxWidth > 0 && frame.Width > (uint)maxWidth)
                    frame.Resize((uint)maxWidth, 0);
                var delay = (int)frame.AnimationDelay * 10;
                frame.Format = MagickFormat.Png;
                frames.Add((frame.ToByteArray(), delay));
            }

            return frames.Count == 0 ? null : frames;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Bitmap?> LoadBitmapAsync(string path, int maxWidth)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (maxWidth > 0)
                    return Bitmap.DecodeToWidth(stream, maxWidth);
                return new Bitmap(stream);
            }
            catch (Exception ex)
            {
                LastError = $"Decode failed: {ex.Message} ({Path.GetFileName(path)})";
                return null;
            }
        });
    }

    private static void ConvertToPng(string sourcePath, string pngPath, int maxWidth)
    {
        using var image = new MagickImage(sourcePath);
        if (maxWidth > 0 && image.Width > (uint)maxWidth)
            image.Resize((uint)maxWidth, 0);
        image.Format = MagickFormat.Png;
        image.Write(pngPath);
    }

    private static bool LooksLikeGifUrl(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(url).AbsolutePath
                : url;
            return string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return url.Contains(".gif", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool LooksLikeGif(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[6];
            using var fs = File.OpenRead(path);
            var n = fs.Read(header);
            if (n < 6) return false;
            var sig = Encoding.ASCII.GetString(header);
            return sig is "GIF87a" or "GIF89a";
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeAvif(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[32];
            using var fs = File.OpenRead(path);
            var n = fs.Read(header);
            if (n < 12) return false;
            if (header[4] != (byte)'f' || header[5] != (byte)'t' || header[6] != (byte)'y' || header[7] != (byte)'p')
                return false;
            var brand = Encoding.ASCII.GetString(header.Slice(8, Math.Min(8, n - 8)));
            return brand.Contains("avif", StringComparison.OrdinalIgnoreCase)
                   || brand.Contains("avis", StringComparison.OrdinalIgnoreCase)
                   || brand.Contains("mif1", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string CachePathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())));
        var ext = GuessExtension(url);
        return Path.Combine(_cacheDir, hash + ext);
    }

    private string ThumbPathFor(string url, int maxWidth)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())));
        return Path.Combine(_cacheDir, $"{hash}.w{maxWidth}.png");
    }

    private static string GuessExtension(string url)
    {
        try
        {
            var path = new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(url).AbsolutePath
                : url;
            var ext = Path.GetExtension(path);
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".avif")
                return ext.ToLowerInvariant();
        }
        catch { /* ignore */ }
        return ".img";
    }

    private static CacheMeta ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return new CacheMeta();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CacheMeta>(json, MetaJson) ?? new CacheMeta();
        }
        catch
        {
            return new CacheMeta();
        }
    }

    private static void WriteMeta(string path, CacheMeta meta)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(meta, MetaJson));
        }
        catch { /* non-fatal */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : value[..77] + "…";

    private sealed class CacheMeta
    {
        public string? SourceVersion { get; set; }
        public string? ETag { get; set; }
    }
}
