using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class KazumiCatalogServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"luatools-kazumi-{Guid.NewGuid():N}");

    [Fact]
    public async Task FindsGameByNormalizedNameAndKeepsMagnet()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "jogos.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "Name": "Pokémon™ Test: Deluxe",
                "downloads": [
                  { "host": "Torrent", "url": "magnet:?xt=urn:btih:ABC", "isTorrent": true }
                ]
              }
            ]
            """);

        var service = new KazumiCatalogService(path);
        var game = await service.FindAsync(1, "Pokemon Test Deluxe");

        Assert.NotNull(game);
        Assert.Single(game.Downloads);
        Assert.True(game.Downloads[0].IsTorrent);
        Assert.StartsWith("magnet:?", game.Downloads[0].Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotUseUnsafePartialTitleMatches()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "jogos.json");
        await File.WriteAllTextAsync(path, """
            [{"Name":"Portal 2","downloads":[{"host":"Web","url":"https://example.com/file","isTorrent":false}]}]
            """);

        var service = new KazumiCatalogService(path);

        Assert.Null(await service.FindAsync(1, "Portal"));
    }

    [Fact]
    public async Task RejectsUnsupportedDownloadSchemes()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "jogos.json");
        await File.WriteAllTextAsync(path, """
            [{"Name":"Game","downloads":[{"host":"Bad","url":"ftp://example.com/file","isTorrent":true}]}]
            """);

        var service = new KazumiCatalogService(path);

        Assert.Null(await service.FindAsync(1, "Game"));
    }

    [Fact]
    public async Task UsesUrlSchemeWhenTorrentFlagIsWrong()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "jogos.json");
        await File.WriteAllTextAsync(path, """
            [{"Name":"Game","downloads":[
              {"host":"Web","url":"https://example.com/file","isTorrent":true},
              {"host":"Torrent","url":"magnet:?xt=urn:btih:ABC","isTorrent":false}
            ]}]
            """);

        var game = await new KazumiCatalogService(path).FindAsync(1, "Game");

        Assert.False(game!.Downloads.Single(d => d.Host == "Web").IsTorrent);
        Assert.True(game.Downloads.Single(d => d.Host == "Torrent").IsTorrent);
    }

    [Fact]
    public async Task SteamAppIdFromHeaderIsPreferredOverTitle()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "jogos.json");
        await File.WriteAllTextAsync(path, """
            [{"Name":"Localized Title","HeaderImage":"https://cdn.test/steam/apps/12345/header.jpg","downloads":[{"host":"Torrent","url":"magnet:?xt=urn:btih:ABC","isTorrent":true}]}]
            """);

        var service = new KazumiCatalogService(path);

        Assert.NotNull(await service.FindAsync(12345, "A completely different Steam title"));
    }

    [Fact]
    public async Task BundledCatalogContainsOnlyParseableFlaggedMagnets()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Kazumi", "jogos.json");
        await using var stream = File.OpenRead(path);
        var games = await JsonSerializer.DeserializeAsync<List<KazumiGame>>(stream);
        var magnets = games!
            .SelectMany(game => game.Downloads)
            .Where(download => download.Url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(250, magnets.Count);
        Assert.All(magnets, download =>
            Assert.True(TorrentDownloadService.IsValidMagnetUri(download.Url), $"Invalid magnet in catalog: {download.Url}"));
    }

    [Fact]
    public async Task BundledCatalogResolvesARealGameBySteamAppId()
    {
        var game = await new KazumiCatalogService().FindAsync(976310, "Title is deliberately different");

        Assert.Equal("Mortal Kombat 11", game?.Name);
        Assert.Contains(game!.Downloads, download => download.IsTorrent && download.Url.StartsWith("magnet:?"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
