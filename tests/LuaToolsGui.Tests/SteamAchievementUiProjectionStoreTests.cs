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
            Assert.Contains("DetailsProgressBar", script);
            Assert.Contains("aria-valuenow", script);
            Assert.Contains("AchievementsOverlayContainer", script);
            Assert.Contains("AchievementListItemBase", script);
            Assert.Contains("__reactFiber$", script);
            Assert.Contains("UnlockDate", script);
            Assert.Contains("achievementBridgeHidden", script);
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

    [Fact]
    public void Save_UsesProjectionTimeWhenSourceHasNoUnlockTime()
    {
        string root = Path.Combine(Path.GetTempPath(), $"luatools-ui-projection-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "projections.json");
        try
        {
            var store = new SteamAchievementUiProjectionStore(path);
            store.Save(99, [Item("ACH_1", earned: true, time: 0)], new HashSet<string>(["ACH_1"]));

            string json = File.ReadAllText(path);
            Assert.DoesNotContain("\"unlocked_at\":0", json);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddConfirmed_PreservesExistingProjectionAndAddsLiveUnlock()
    {
        string root = Path.Combine(Path.GetTempPath(), $"luatools-ui-projection-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "projections.json");
        try
        {
            var store = new SteamAchievementUiProjectionStore(path);
            Achievement[] initial =
            [
                Item("ACH_1", earned: true, time: 100),
                Item("ACH_2", earned: false, time: null),
            ];
            store.Save(99, initial, new HashSet<string>(["ACH_1"]));

            store.AddConfirmed(99, initial, "ACH_2", 200);

            string json = File.ReadAllText(path);
            Assert.Contains("\"achieved_count\":2", json);
            Assert.Contains("\"api_name\":\"ACH_1\"", json);
            Assert.Contains("\"api_name\":\"ACH_2\"", json);
            Assert.Contains("\"unlocked_at\":200", json);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Achievement Item(string id, bool earned, long? time) => new(
        id, id, $"Description {id}", $"https://example.test/{id}.jpg", null,
        earned, time, "uplay_r2", false, 10);
}
