using System.IO;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class SteamAchievementUiProjectionStoreTests
{
    [Fact]
    public void Save_PersistsOnlyConfirmedEarnedAchievements()
    {
        string root = Path.Combine(Path.GetTempPath(), $"luatools-ui-projection-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "projections.json");
        try
        {
            var store = new SteamAchievementUiProjectionStore(path);
            Achievement[] catalog =
            [
                Item("ACH_1", earned: true, time: 100),
                Item("ACH_2", earned: true, time: 200),
                Item("ACH_3", earned: false, time: null),
            ];

            store.Save(99, catalog, new HashSet<string>(["ACH_2"], StringComparer.OrdinalIgnoreCase));
            var reloaded = new SteamAchievementUiProjectionStore(path);
            string script = reloaded.BuildPatchScript();

            Assert.Contains("\"app_id\":99", File.ReadAllText(path));
            Assert.Contains("\"achieved_count\":1", script);
            Assert.Contains("ACH_2", script);
            Assert.DoesNotContain("ACH_1", script);
            Assert.Contains("AchievementProgress", script);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildPatchScript_IsEmptyWithoutProjections()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "projections.json");
        var store = new SteamAchievementUiProjectionStore(path);
        Assert.Equal("", store.BuildPatchScript());
    }

    private static Achievement Item(string id, bool earned, long? time) => new(
        id, id, $"Description {id}", $"https://example.test/{id}.jpg", null,
        earned, time, "uplay_r2", false, 10);
}
