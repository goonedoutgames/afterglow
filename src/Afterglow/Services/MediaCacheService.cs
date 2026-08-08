using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>Loads hub/F95 images with auth, disk cache, Magick AVIF/GIF fallback, and diagnostics.</summary>
public sealed class MediaCacheService
{
    private readonly HubApiClient _hub;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Bitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AnimatedMedia> _animated = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheDir;

    public MediaCacheService(HubApiClient hub)
    {
        _hub = hub;
        _cacheDir = Path.Combine(AppPaths.Root, "media-cache");
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

    public async Task<Bitmap?> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        var media = await GetMediaAsync(url, preferAnimation: false, cancellationToken);
        return media?.Preview;
    }

    public Task<AnimatedMedia?> GetMediaAsync(string? url, CancellationToken cancellationToken = default) =>
        GetMediaAsync(url, preferAnimation: true, cancellationToken);

    private async Task<AnimatedMedia?> GetMediaAsync(string? url, bool preferAnimation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            FailureCount++;
            LastError ??= "No media URL from hub (cover_url empty). Refresh the game metadata.";
            return null;
        }

        var key = url.Trim();
        if (preferAnimation && _animated.TryGetValue(key, out var animatedHit))
        {
            SuccessCount++;
            return animatedHit;
        }

        if (!preferAnimation && _bitmaps.TryGetValue(key, out var bmpHit))
        {
            SuccessCount++;
            return new AnimatedMedia { Preview = bmpHit };
        }

        if (_animated.TryGetValue(key, out animatedHit))
        {
            SuccessCount++;
            return animatedHit;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (preferAnimation && _animated.TryGetValue(key, out animatedHit))
            {
                SuccessCount++;
                return animatedHit;
            }

            if (_bitmaps.TryGetValue(key, out bmpHit) && !preferAnimation)
            {
                SuccessCount++;
                return new AnimatedMedia { Preview = bmpHit };
            }

            var rawPath = CachePathFor(key);
            var pngPath = Path.ChangeExtension(rawPath, ".png");

            if (!File.Exists(rawPath))
            {
                var (bytes, error, status) = await _hub.DownloadBytesDetailedAsync(key, cancellationToken);
                if (bytes is null || bytes.Length < 24)
                {
                    FailureCount++;
                    LastError = error ?? $"Empty media response (HTTP {status}) for {Truncate(key)}";
                    return null;
                }

                if (bytes[0] == (byte)'<' || bytes[0] == (byte)'{' || bytes[0] == (byte)'[')
                {
                    FailureCount++;
                    LastError = $"Media URL returned non-image payload (HTTP {status}) for {Truncate(key)}";
                    return null;
                }

                await File.WriteAllBytesAsync(rawPath, bytes, cancellationToken);
            }

            var isGif = LooksLikeGif(rawPath)
                        || string.Equals(Path.GetExtension(rawPath), ".gif", StringComparison.OrdinalIgnoreCase);

            if (preferAnimation && isGif)
            {
                var decoded = await Task.Run(() => DecodeGifBytes(rawPath), cancellationToken);
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
                    _animated[key] = gif;
                    _bitmaps[key] = gif.Preview;
                    SuccessCount++;
                    return gif;
                }
            }

            // Prefer a previously converted PNG (AVIF/etc. that Avalonia cannot decode).
            if (File.Exists(pngPath))
            {
                var fromPng = await LoadBitmapAsync(pngPath);
                if (fromPng is not null)
                {
                    var media = new AnimatedMedia { Preview = fromPng };
                    _bitmaps[key] = fromPng;
                    _animated[key] = media;
                    SuccessCount++;
                    return media;
                }
            }

            Bitmap? bitmap = null;
            var needsMagick = LooksLikeAvif(rawPath)
                              || string.Equals(Path.GetExtension(rawPath), ".avif", StringComparison.OrdinalIgnoreCase)
                              || isGif;
            if (!needsMagick)
                bitmap = await LoadBitmapAsync(rawPath);

            if (bitmap is null)
            {
                try
                {
                    await Task.Run(() => ConvertToPng(rawPath, pngPath), cancellationToken);
                    bitmap = await LoadBitmapAsync(pngPath);
                    // Keep raw GIF so animation can decode later; AVIF/etc. can drop the raw copy.
                    if (bitmap is not null && !isGif)
                    {
                        try { File.Delete(rawPath); } catch { /* ignore */ }
                    }
                }
                catch (Exception ex)
                {
                    LastError = $"Decode failed: {ex.Message} ({Path.GetFileName(rawPath)})";
                    try { File.Delete(rawPath); } catch { /* ignore */ }
                    try { File.Delete(pngPath); } catch { /* ignore */ }
                }
            }

            if (bitmap is null)
            {
                FailureCount++;
                LastError ??= $"Decode failed ({Path.GetFileName(rawPath)})";
                return null;
            }

            var result = new AnimatedMedia { Preview = bitmap };
            _bitmaps[key] = bitmap;
            _animated[key] = result;
            SuccessCount++;
            return result;
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

    private static List<(byte[] Png, int DelayMs)>? DecodeGifBytes(string path)
    {
        try
        {
            using var collection = new MagickImageCollection(path);
            if (collection.Count == 0) return null;
            collection.Coalesce();

            var frames = new List<(byte[] Png, int DelayMs)>(collection.Count);
            foreach (var frame in collection)
            {
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

    private async Task<Bitmap?> LoadBitmapAsync(string path)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { return new Bitmap(path); }
            catch (Exception ex)
            {
                LastError = $"Decode failed: {ex.Message} ({Path.GetFileName(path)})";
                return null;
            }
        });
    }

    private static void ConvertToPng(string sourcePath, string pngPath)
    {
        using var image = new MagickImage(sourcePath);
        image.Format = MagickFormat.Png;
        image.Write(pngPath);
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

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : value[..77] + "…";
}
