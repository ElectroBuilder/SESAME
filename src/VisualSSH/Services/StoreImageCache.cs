using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SixLabors.ImageSharp;

namespace VisualSSH.Services;

public static class StoreImageCache
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string Dir = AppDataPaths.CacheDir("store-cache");

    public static async Task<BitmapImage?> LoadAsync(string? url, int decodeWidth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || IsPlaceholder(url)) return null;
        var absolute = NormalizeUrl(url);
        if (absolute is null) return null;

        Directory.CreateDirectory(Dir);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(absolute)))[..16].ToLowerInvariant();
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        string? path;
        await gate.WaitAsync(ct);
        try
        {
            path = await EnsureFileAsync(absolute, key, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            gate.Release();
        }

        if (string.IsNullOrEmpty(path)) return null;
        return await CreateBitmapAsync(path, decodeWidth, ct);
    }

    public static bool IsPlaceholder(string url) => StoreUrls.IsPlaceholder(url);

    public static string? NormalizeUrl(string? url) => StoreUrls.NormalizeUrl(url);

    private static async Task<string?> EnsureFileAsync(string url, string key, CancellationToken ct)
    {
        var existing = FindCached(key);
        if (existing is not null)
        {
            var ready = await PrepareForWicAsync(existing, key, ct);
            if (ready is not null) return ready;
            TryDelete(existing);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.Host.Contains("gamebanana.com", StringComparison.OrdinalIgnoreCase))
            request.Headers.Referrer = new Uri("https://gamebanana.com/");
        else if (uri.Host.Contains("archive.org", StringComparison.OrdinalIgnoreCase))
            request.Headers.Referrer = new Uri("https://archive.org/");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < 24 || LooksLikeHtml(bytes)) return null;

        var ext = DetectExt(bytes) ?? GuessExt(uri.AbsolutePath);
        var path = Path.Combine(Dir, key + ext);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        return await PrepareForWicAsync(path, key, ct) ?? path;
    }

    private static async Task<string?> PrepareForWicAsync(string path, string key, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length < 24 || LooksLikeHtml(bytes)) return null;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if ((ext is ".jpg" or ".jpeg" or ".png" or ".gif") && HasImageMagic(bytes))
                return path;

            return await Task.Run(() =>
            {
                using var image = Image.Load(bytes);
                var png = Path.Combine(Dir, key + ".png");
                var tmp = png + ".tmp";
                image.SaveAsPng(tmp);
                File.Move(tmp, png, overwrite: true);
                if (!string.Equals(path, png, StringComparison.OrdinalIgnoreCase))
                    TryDelete(path);
                return png;
            }, ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> CreateBitmapAsync(string path, int decodeWidth, CancellationToken ct)
    {
        BitmapImage? Create()
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                return Create();
            return await dispatcher.InvokeAsync(Create, DispatcherPriority.Background).Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private static string? FindCached(string key)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".img" })
        {
            var path = Path.Combine(Dir, key + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* cache opruimen is optioneel */ }
    }

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var probe = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64)).TrimStart();
        return probe.StartsWith("<!", StringComparison.OrdinalIgnoreCase) ||
               probe.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               probe.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasImageMagic(byte[] b)
    {
        if (b.Length < 12) return false;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;
        if (b[0] == 0x42 && b[1] == 0x4D) return true;
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;
        return false;
    }

    private static string? DetectExt(byte[] b)
    {
        if (b.Length >= 12 && b[0] == 0x52 && b[8] == 0x57) return ".webp";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return ".png";
        if (b.Length >= 3 && b[0] == 0x47 && b[1] == 0x49) return ".gif";
        return null;
    }

    private static string GuessExt(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" ? ext : ".img";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/*,*/*;q=0.8");
        return client;
    }
}
