using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Loads the user-provided Kazumi catalog and resolves Steam titles without fuzzy matches.</summary>
public sealed class KazumiCatalogService
{
    private readonly string _catalogPath;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CatalogIndex? _index;

    public KazumiCatalogService()
        : this(Path.Combine(AppContext.BaseDirectory, "Kazumi", "jogos.json")) { }

    internal KazumiCatalogService(string catalogPath) => _catalogPath = catalogPath;

    public async Task<KazumiGame?> FindAsync(long steamAppId, string gameName, CancellationToken ct = default)
    {
        var catalog = await LoadAsync(ct);
        if (catalog.ByAppId.TryGetValue(steamAppId, out var byId)) return byId;
        catalog.ByNormalizedName.TryGetValue(NormalizeName(gameName), out var game);
        return game;
    }

    private async Task<CatalogIndex> LoadAsync(CancellationToken ct)
    {
        if (_index is not null) return _index;

        await _loadGate.WaitAsync(ct);
        try
        {
            if (_index is not null) return _index;
            if (!File.Exists(_catalogPath))
                return _index = new CatalogIndex(
                    new Dictionary<long, KazumiGame>(),
                    new Dictionary<string, KazumiGame>());

            await using var stream = File.OpenRead(_catalogPath);
            var games = await JsonSerializer.DeserializeAsync<List<KazumiGame>>(stream, cancellationToken: ct) ?? [];
            var byAppId = new Dictionary<long, KazumiGame>();
            var byName = new Dictionary<string, KazumiGame>(StringComparer.Ordinal);

            foreach (var game in games)
            {
                if (string.IsNullOrWhiteSpace(game.Name)) continue;
                string key = NormalizeName(game.Name);
                if (key.Length == 0) continue;

                game.Downloads = game.Downloads
                    .Where(NormalizeDownload)
                    .GroupBy(d => d.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                if (game.Downloads.Count == 0) continue;

                Merge(byName, key, game);
                if (TryGetSteamAppId(game.HeaderImageUrl) is { } appId)
                    Merge(byAppId, appId, game);
            }

            return _index = new CatalogIndex(byAppId, byName);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static void Merge<TKey>(Dictionary<TKey, KazumiGame> index, TKey key, KazumiGame game)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var existing))
        {
            index[key] = game;
            return;
        }

        existing.Downloads = existing.Downloads
            .Concat(game.Downloads)
            .GroupBy(d => d.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static long? TryGetSteamAppId(string? headerImage)
    {
        if (string.IsNullOrWhiteSpace(headerImage)) return null;
        var match = Regex.Match(headerImage, @"/apps/(\d+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out long appId) ? appId : null;
    }

    internal static string NormalizeName(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) normalized.Append(char.ToLowerInvariant(c));
        }
        return normalized.ToString();
    }

    private static bool NormalizeDownload(KazumiDownload download)
    {
        if (string.IsNullOrWhiteSpace(download.Url)) return false;
        if (download.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            download.IsTorrent = true;
            return true;
        }

        download.IsTorrent = false;
        return Uri.TryCreate(download.Url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private sealed record CatalogIndex(
        IReadOnlyDictionary<long, KazumiGame> ByAppId,
        IReadOnlyDictionary<string, KazumiGame> ByNormalizedName);
}
