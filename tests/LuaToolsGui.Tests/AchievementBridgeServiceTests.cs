using LuaToolsGui.Services;
using System.IO;
using Xunit;

namespace LuaToolsGui.Tests;

public class AchievementBridgeServiceTests
{
    [Fact]
    public async Task BurstGate_AllowsSingleGameplayEvent()
    {
        var gate = new AchievementBurstGate(TimeSpan.FromMilliseconds(20), threshold: 3);

        Assert.False(await gate.IsBackfillAsync("uplay_r2:66088"));
    }

    [Fact]
    public async Task BurstGate_SuppressesSchemaHydrationBatch()
    {
        var gate = new AchievementBurstGate(TimeSpan.FromMilliseconds(30), threshold: 3);

        bool[] results = await Task.WhenAll(
            gate.IsBackfillAsync("uplay_r2:66088"),
            gate.IsBackfillAsync("uplay_r2:66088"),
            gate.IsBackfillAsync("uplay_r2:66088"),
            gate.IsBackfillAsync("uplay_r2:66088"));

        Assert.All(results, Assert.True);
    }

    [Fact]
    public void EventParser_ReadsAnUnlockedR2Envelope()
    {
        var parser = new AchievementBridgeEventParser();
        Assert.Null(parser.PushLine("[UplayR2Provider] status=watching"));
        Assert.Null(parser.PushLine("[AchievementBridge]"));
        Assert.Null(parser.PushLine("provider=uplay_r2"));
        Assert.Null(parser.PushLine("product_id=66088"));
        Assert.Null(parser.PushLine("achievement=10"));
        Assert.Null(parser.PushLine("state=unlocked"));
        Assert.Null(parser.PushLine("timestamp=1787390253"));
        Assert.Null(parser.PushLine("recovered=false"));

        AchievementBridgeEvent? achievement = parser.PushLine("");

        Assert.NotNull(achievement);
        Assert.Equal("uplay_r2", achievement.Provider);
        Assert.Equal(66088, achievement.ProductId);
        Assert.Equal("10", achievement.Achievement);
        Assert.Equal(1787390253, achievement.Timestamp);
        Assert.False(achievement.Recovered);
    }

    [Fact]
    public void EventParser_ReadsAnUnlockedRuneEnvelope()
    {
        var parser = new AchievementBridgeEventParser();
        Assert.Null(parser.PushLine("[AchievementBridge]"));
        Assert.Null(parser.PushLine("provider=rune"));
        Assert.Null(parser.PushLine("appid=3046600"));
        Assert.Null(parser.PushLine("achievement=ACHIEVEMENT_03"));
        Assert.Null(parser.PushLine("state=unlocked"));
        Assert.Null(parser.PushLine("timestamp=1788325557"));
        Assert.Null(parser.PushLine("recovered=false"));

        AchievementBridgeEvent? achievement = parser.PushLine("");

        Assert.NotNull(achievement);
        Assert.Equal("rune", achievement.Provider);
        Assert.Equal(3046600, achievement.AppId);
        Assert.Equal("ACHIEVEMENT_03", achievement.Achievement);
        Assert.Equal(1788325557, achievement.Timestamp);
        Assert.False(achievement.Recovered);
    }

    [Fact]
    public void FindExecutable_PrefersDevelopmentOverride()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"achievement-bridge-{Guid.NewGuid():N}.exe");
        string? previous = Environment.GetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH");
        try
        {
            File.WriteAllBytes(tempFile, []);
            Environment.SetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH", tempFile);
            Assert.Equal(tempFile, AchievementBridgeService.FindExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH", previous);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CloudHostConfig_EnablesStandaloneProxyAndPreservesOtherTables()
    {
        string[] configured = AchievementBridgeSetupService.ConfigureOpenSteamTool(
        [
            "[lua]",
            "paths = [\"config/stplug-in\"]",
            "",
            "[cloud]",
            "enabled = false",
            "library = \"cloud_redirect.dll\"",
            "",
            "[remote]",
            "provider = \"github\"",
        ]);

        Assert.Contains("enabled = true", configured);
        Assert.Contains("library = \"AchievementBridge/achievement-bridge-cloud.dll\"", configured);
        Assert.Contains("provider = \"github\"", configured);
        Assert.Equal(1, configured.Count(line => line.StartsWith("library =", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("store_queued", true)]
    [InlineData("progress_queued", true)]
    [InlineData("set_failed", false)]
    [InlineData("progress_failed", false)]
    [InlineData("not_new", false)]
    [InlineData("not_requested", false)]
    public void NativeSteamNotification_OnlySuppressesPopupWhenNativeToastWasQueued(string status, bool expected)
    {
        Assert.Equal(expected, AchievementBridgeService.ShouldSuppressLuaToolsPopup(status));
    }

    [Theory]
    [InlineData(true, false, true, true, false, "sync_unconfirmed", true)]
    [InlineData(true, false, true, false, true, "not_requested", true)]
    [InlineData(true, false, true, false, false, "sync_unconfirmed", false)]
    [InlineData(true, false, false, true, true, "not_new", false)]
    [InlineData(true, true, true, true, true, "not_requested", false)]
    [InlineData(false, false, true, true, true, "not_requested", false)]
    [InlineData(true, false, true, true, true, "progress_queued", false)]
    public void Popup_RequiresConfirmedNewSteamCacheOrApiState(
        bool enabled,
        bool recovered,
        bool changed,
        bool cacheConfirmed,
        bool steamConfirmed,
        string nativeNotification,
        bool expected)
    {
        Assert.Equal(expected, AchievementBridgeService.ShouldShowLuaToolsPopup(
            enabled, recovered, changed, cacheConfirmed, steamConfirmed, nativeNotification));
    }
}
