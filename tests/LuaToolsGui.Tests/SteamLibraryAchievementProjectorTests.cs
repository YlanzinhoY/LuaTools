using System.IO;
using System.Text.Json.Nodes;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class SteamLibraryAchievementProjectorTests
{
    [Fact]
    public void Project_WritesOnlyConfirmedUnlocksAndCreatesBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), $"luatools-projector-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "userdata", "42", "config", "librarycache");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "99.json");
        File.WriteAllText(path,
            "[[\"achievements\",{\"version\":2,\"data\":{\"vecHighlight\":[],\"vecUnachieved\":[],\"vecAchievedHidden\":[],\"nTotal\":3,\"nAchieved\":0}}]]");
        try
        {
            var projector = new SteamLibraryAchievementProjector(() => root, () => false);
            Achievement[] catalog =
            [
                Item("ACH_1", earned: true, time: 100, percent: 10),
                Item("ACH_2", earned: true, time: 200, percent: 20),
                Item("ACH_3", earned: false, time: null, percent: 30),
            ];

            AchievementLibraryProjectionResult result = projector.Project(
                99,
                42,
                catalog,
                new HashSet<string>(["ACH_2"], StringComparer.OrdinalIgnoreCase));

            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(1, result.AchievedCount);
            JsonArray document = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            JsonObject data = document[0]![1]!["data"]!.AsObject();
            Assert.Equal(1, data["nAchieved"]!.GetValue<int>());
            Assert.Equal("ACH_2", data["vecHighlight"]![0]!["strID"]!.GetValue<string>());
            Assert.True(data["vecHighlight"]![0]!["bAchieved"]!.GetValue<bool>());
            Assert.Equal(2, data["vecUnachieved"]!.AsArray().Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Project_RefusesToWriteWhileSteamIsRunning()
    {
        var projector = new SteamLibraryAchievementProjector(() => "unused", () => true);
        Assert.Throws<InvalidOperationException>(() => projector.Project(
            99,
            42,
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    private static Achievement Item(string id, bool earned, long? time, double percent) => new(
        id,
        id,
        $"Description {id}",
        $"https://example.test/{id}.jpg",
        null,
        earned,
        time,
        "uplay_r2",
        false,
        percent);
}
