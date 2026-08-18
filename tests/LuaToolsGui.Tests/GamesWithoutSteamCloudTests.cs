using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class GamesWithoutSteamCloudTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LuaToolsGui.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _saveRelative = Path.Combine("LuaToolsGui.Tests", Guid.NewGuid().ToString("N"));

    public GamesWithoutSteamCloudTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task PreparedSaveDirectoryIncludesOnlyMatchingTopLevelSaveFiles()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _saveRelative);
        Directory.CreateDirectory(Path.Combine(root, "manual_backup"));
        await File.WriteAllTextAsync(Path.Combine(root, "slot.save"), "active");
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "ignore");
        await File.WriteAllTextAsync(Path.Combine(root, "manual_backup", "slot.save"), "duplicate");

        var game = new GameSaveDefinition
        {
            AppId = 3751950,
            Name = "Black Flag Resynced",
            SaveLocations =
            [
                new GameSaveLocation
                {
                    Base = "RoamingAppData",
                    RelativePath = _saveRelative,
                    IncludePatterns = ["*.save"],
                    Recursive = false,
                }
            ],
        };

        var service = new SaveBackupService(new GameSaveResolver());
        using var saveFiles = await service.CreateSaveFilesAsync(game);

        Assert.NotNull(saveFiles);
        Assert.Equal(1, saveFiles.FileCount);
        Assert.True(File.Exists(Path.Combine(saveFiles.DirectoryPath, "saves", "slot.save")));
        Assert.True(File.Exists(Path.Combine(saveFiles.DirectoryPath, "_cloudredirect-save.json")));
        Assert.False(Directory.Exists(Path.Combine(saveFiles.DirectoryPath, "saves", "manual_backup")));
        Assert.False(File.Exists(Path.Combine(saveFiles.DirectoryPath, "saves", "notes.txt")));

        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(saveFiles.DirectoryPath, "_cloudredirect-save.json")));
        Assert.Equal(2, manifest.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(3751950, manifest.RootElement.GetProperty("appId").GetInt64());
    }

    [Fact]
    public async Task RestoreReplacesTheActiveSaveFromAValidatedSnapshot()
    {
        string relative = Path.Combine(_saveRelative, "restore");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative);
        Directory.CreateDirectory(root);
        string save = Path.Combine(root, "slot.save");
        await File.WriteAllTextAsync(save, "known-good");
        var location = new GameSaveLocation
        {
            Base = "RoamingAppData",
            RelativePath = relative,
            IncludePatterns = ["*.save"],
            Recursive = false,
        };
        var game = new GameSaveDefinition
        {
            AppId = 3751950,
            Name = "Black Flag Resynced",
            SaveVariants =
            [
                new GameSaveVariant
                {
                    Id = "voices38",
                    Name = "voices38",
                    SaveLocations = [location],
                }
            ],
        };
        var resolver = new GameSaveResolver();
        var backup = new SaveBackupService(resolver);
        var restore = new SaveRestoreService(resolver);
        using var saveFiles = await backup.CreateSaveFilesAsync(game.ForVariant(game.SaveVariants[0]));
        Assert.NotNull(saveFiles);

        await File.WriteAllTextAsync(save, "corrupted");
        var result = await restore.RestoreAsync(game, saveFiles.DirectoryPath);

        Assert.True(result.Success, result.Error);
        Assert.Equal("known-good", await File.ReadAllTextAsync(save));
    }

    [Fact]
    public void ShippingCatalogContainsBlackFlagResynced()
    {
        var registry = new GamesWithoutSteamCloud();

        var match = registry.Find(3751950);
        Assert.NotNull(match);
        Assert.Equal("assassins-creed", match.Module.Id);
        Assert.Equal("Assassin's Creed Black Flag Resynced", match.Game!.Name);
        Assert.Empty(match.Game.SaveLocations);
        Assert.Collection(match.Game.SaveVariants,
            voices38 =>
            {
                Assert.Equal("voices38", voices38.Id);
                Assert.Equal("Voices38", voices38.Name);
                var location = Assert.Single(voices38.SaveLocations);
                Assert.Equal("RoamingAppData", location.Base);
                Assert.Equal("Goldberg UplayEmu Saves/66088", location.RelativePath);
                Assert.Equal(["*.save"], location.IncludePatterns);
                Assert.False(location.Recursive);
            },
            hv =>
            {
                Assert.Equal("hv", hv.Id);
                Assert.Equal("HV", hv.Name);
                var location = Assert.Single(hv.SaveLocations);
                Assert.Equal("SteamInstall", location.Base);
                Assert.Equal("saves/65043", location.RelativePath);
                Assert.Equal(["*.save"], location.IncludePatterns);
                Assert.False(location.Recursive);
            });
    }

    [Fact]
    public async Task HvVariantResolvesSavesRelativeToTheSteamInstallDirectory()
    {
        string saveRoot = Path.Combine(_dir, "saves", "65043");
        Directory.CreateDirectory(saveRoot);
        await File.WriteAllTextAsync(Path.Combine(saveRoot, "slot.save"), "hv-save");
        await File.WriteAllTextAsync(Path.Combine(saveRoot, "info.txt"), "ignore");
        var variant = new GameSaveVariant
        {
            Id = "hv",
            Name = "HV",
            SaveLocations =
            [
                new GameSaveLocation
                {
                    Base = "SteamInstall",
                    RelativePath = "saves/65043",
                    IncludePatterns = ["*.save"],
                    Recursive = false,
                }
            ],
        };
        var game = new GameSaveDefinition
        {
            AppId = 3751950,
            Name = "Assassin's Creed Black Flag Resynced",
            SaveVariants = [variant],
        };
        var resolver = new GameSaveResolver(appId => appId == game.AppId ? _dir : null);
        var resolved = Assert.Single(resolver.ResolveExistingLocations(game.ForVariant(variant)));

        Assert.Equal(saveRoot, resolved.RootPath);
        Assert.Equal([Path.Combine(saveRoot, "slot.save")], resolved.Files);

        using var saveFiles = await new SaveBackupService(resolver).CreateSaveFilesAsync(game.ForVariant(variant));
        Assert.NotNull(saveFiles);
        Assert.True(File.Exists(Path.Combine(saveFiles.DirectoryPath, "saves", "slot.save")));
        Assert.False(File.Exists(Path.Combine(saveFiles.DirectoryPath, "saves", "info.txt")));
    }

    [Fact]
    public void FindsAppIdAndItsDetailedSaveDefinition()
    {
        File.WriteAllText(Path.Combine(_dir, "assassins-creed.json"), """
        {
          "id": "assassins-creed",
          "name": "Assassin's Creed",
          "launcher": "Ubisoft Connect",
          "appIds": [3751950],
          "games": [{
            "appid": 3751950,
            "name": "Assassin's Creed Black Flag Resynced",
            "executables": ["ACBF.exe"],
            "saveLocations": [
              { "platform": "windows", "base": "LocalAppData", "relativePath": "Ubisoft/Game/save" },
              { "platform": "windows", "base": "ProgramFilesX86", "relativePath": "Ubisoft/savegames/*/66088" }
            ]
          }]
        }
        """);

        var registry = new GamesWithoutSteamCloud(_dir);

        var match = registry.Find(3751950);
        Assert.NotNull(match);
        Assert.Equal("assassins-creed", match.Module.Id);
        Assert.Equal("Assassin's Creed Black Flag Resynced", match.Game!.Name);
        Assert.Equal(2, match.Game.SaveLocations.Count);
        Assert.Null(registry.Find(123));
    }

    [Fact]
    public void ArrayOnlyAppIdIsSupportedBeforeSaveMetadataExists()
    {
        File.WriteAllText(Path.Combine(_dir, "ea.json"), """
        { "id": "ea", "name": "EA", "appIds": [10, 20], "games": [] }
        """);

        var registry = new GamesWithoutSteamCloud(_dir);

        Assert.True(registry.Contains(20));
        Assert.Null(registry.Find(20)!.Game);
    }

    [Fact]
    public void RejectsAnAppIdOwnedByTwoModules()
    {
        File.WriteAllText(Path.Combine(_dir, "a.json"),
            """{ "id": "a", "name": "A", "appIds": [3751950] }""");
        File.WriteAllText(Path.Combine(_dir, "b.json"),
            """{ "id": "b", "name": "B", "appIds": [3751950] }""");

        var error = Assert.Throws<InvalidDataException>(() => new GamesWithoutSteamCloud(_dir));
        Assert.Contains("more than one module", error.Message);
    }

    [Fact]
    public void RejectsDetailedGameMissingFromTheModuleArray()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.json"), """
        {
          "id": "bad", "name": "Bad", "appIds": [],
          "games": [{ "appid": 3751950, "name": "Game" }]
        }
        """);

        Assert.Throws<InvalidDataException>(() => new GamesWithoutSteamCloud(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try
        {
            string saveRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _saveRelative);
            Directory.Delete(saveRoot, recursive: true);
        }
        catch { }
    }
}
