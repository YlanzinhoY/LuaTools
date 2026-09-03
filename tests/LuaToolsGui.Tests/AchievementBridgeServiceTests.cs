using LuaToolsGui.Services;
using System.IO;
using Xunit;

namespace LuaToolsGui.Tests;

public class AchievementBridgeServiceTests
{
    [Fact]
    public void ActivityLog_TracksBridgeAndActiveGameStateInUtf8()
    {
        string path = Path.Combine(Path.GetTempPath(), $"achievement-bridge-log-{Guid.NewGuid():N}.log");
        try
        {
            var logs = new AchievementBridgeLogService(path);
            logs.BridgeStarted(321);
            logs.CaptureBridgeLine("[GameSession] pid=42 appid=3046600 name=Onimusha 2: Samurai's Destiny state=watching");
            logs.CaptureBridgeLine("  provider=rune confidence=100 active=true");
            logs.CaptureBridgeLine("[AchievementBridge] active_game_sessions=1");
            logs.Warning("Teste", "Conquista não sincronizada");

            Assert.True(logs.IsRunning);
            Assert.Equal(321, logs.ProcessId);
            Assert.Equal(1, logs.ActiveGameCount);
            Assert.Contains(logs.Snapshot(), entry =>
                entry.Category == "Game" && entry.Message.Contains("Onimusha 2", StringComparison.Ordinal));
            Assert.Contains(logs.Snapshot(), entry =>
                entry.Category == "Provider" && entry.Message.Contains("provider=rune", StringComparison.Ordinal));
            Assert.Contains("Conquista não sincronizada", File.ReadAllText(path, System.Text.Encoding.UTF8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ActivityLog_DoesNotRenderStructuredEventEnvelopeAsNoise()
    {
        string path = Path.Combine(Path.GetTempPath(), $"achievement-bridge-log-{Guid.NewGuid():N}.log");
        try
        {
            var logs = new AchievementBridgeLogService(path);
            logs.CaptureBridgeLine("[AchievementBridge]");
            logs.CaptureBridgeLine("provider=rune");
            logs.CaptureBridgeLine("appid=3046600");
            logs.CaptureBridgeLine("achievement=ACHIEVEMENT_03");

            Assert.Empty(logs.Snapshot());
        }
        finally
        {
            File.Delete(path);
        }
    }

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

    [Theory]
    [InlineData(true, true, 3, 3, true)]
    [InlineData(false, true, 3, 3, false)]
    [InlineData(true, false, 3, 3, false)]
    [InlineData(true, true, 2, 3, false)]
    public void Watchdog_OnlyRestartsTheCurrentEnabledGeneration(
        bool serviceStarted,
        bool achievementsEnabled,
        int processGeneration,
        int currentGeneration,
        bool expected)
    {
        Assert.Equal(expected, AchievementBridgeService.ShouldRestartBridge(
            serviceStarted, achievementsEnabled, processGeneration, currentGeneration));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 30)]
    [InlineData(20, 30)]
    public void Watchdog_UsesBoundedExponentialBackoff(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AchievementBridgeService.RestartDelay(attempt));
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
