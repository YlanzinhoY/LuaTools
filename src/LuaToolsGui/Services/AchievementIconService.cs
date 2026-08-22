using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LuaToolsGui.Services;

/// <summary>Downloads Steam achievement icons once and returns frozen WPF images safe for binding.</summary>
public sealed class AchievementIconService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads = new(6, 6);
    private readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LuaToolsGui", "achievement-icons");

    public Task<ImageSource?> GetAsync(string? url) => string.IsNullOrWhiteSpace(url)
        ? Task.FromResult<ImageSource?>(null)
        : _memory.GetOrAdd(url, LoadCoreAsync);

    private async Task<ImageSource?> LoadCoreAsync(string url)
    {
        try
        {
            string cachePath = Path.Combine(_cacheDir, CacheName(url));
            byte[] bytes;
            if (File.Exists(cachePath))
            {
                bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            }
            else
            {
                await _downloads.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Another request may have populated the file while this one waited.
                    if (File.Exists(cachePath))
                    {
                        bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
                    }
                    else
                    {
                        bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                        Directory.CreateDirectory(_cacheDir);
                        await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _downloads.Release();
                }
            }

            return Decode(bytes);
        }
        catch
        {
            return null; // The list remains useful when offline or if the CDN rejects an icon.
        }
    }

    private static ImageSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string CacheName(string url)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return $"{Convert.ToHexString(hash)}.img";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LuaTools/1.1");
        return client;
    }
}
