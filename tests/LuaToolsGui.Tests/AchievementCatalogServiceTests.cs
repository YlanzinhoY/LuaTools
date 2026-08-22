using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class AchievementCatalogServiceTests
{
    [Fact]
    public void BridgeCatalog_ParsesIconsAndLocalState()
    {
        const string json = """
            {"schema_version":1,"kind":"achievement_catalog","app_id":2542020,"metadata_source":"steam_client","achievements":[{"api_name":"1","name":"Don't go there!","description":"Desc","icon":"hash.jpg","icon_gray":null,"unlocked":true,"unlocked_at":123,"state_source":"local_store","hidden":false,"global_percent":93.4}]}
            """;

        AchievementCatalog catalog = AchievementBridgeClient.ParseCatalog(json, @"C:\local.json");

        Achievement item = Assert.Single(catalog.Achievements);
        Assert.Equal(2542020, catalog.AppId);
        Assert.Equal("Don't go there!", item.DisplayName);
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/2542020/hash.jpg", item.IconUrl);
        Assert.True(item.Earned);
        Assert.Equal("local_store", item.StateSource);
        Assert.Equal(93.4, item.GlobalPercent);
    }

    [Fact]
    public void MergeR2_PreservesSteamMetadataAndPromotesLocalState()
    {
        var steam = new AchievementCatalog(3751950, "steam_client", null, false,
        [
            new Achievement("ACObsidian_Ach_10", "Steam name", "Steam description", "steam.jpg", null,
                false, null, "steam_client", false, 10),
        ]);
        var r2 = new R2AchievementCatalog(3751950, 66088, @"C:\r2.json", true,
        [
            new R2Achievement(10, "ACObsidian_Ach_10", "R2 name", "R2 description", "r2.jpg", true, 456),
        ]);

        AchievementCatalog merged = AchievementCatalogService.MergeR2(steam, r2);

        Achievement item = Assert.Single(merged.Achievements);
        Assert.Equal("Steam name", item.DisplayName);
        Assert.Equal("steam.jpg", item.IconUrl);
        Assert.True(item.Earned);
        Assert.Equal(456, item.EarnedTime);
        Assert.Equal("uplay_r2", item.StateSource);
        Assert.Equal(@"C:\r2.json", merged.StatePath);
    }
}
