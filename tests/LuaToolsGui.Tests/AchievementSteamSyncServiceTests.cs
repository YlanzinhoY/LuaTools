using LuaToolsGui.Models;
using LuaToolsGui.Services;
using System.IO;
using Xunit;

namespace LuaToolsGui.Tests;

public class AchievementSteamSyncServiceTests
{
    [Fact]
    public async Task BatchSync_OnlySendsEarnedItemsWithoutHistoricalToasts()
    {
        var calls = new List<(string ApiName, long Timestamp, bool Notification)>();
        var service = new AchievementSteamSyncService((appId, apiName, timestamp, notification, _) =>
        {
            calls.Add((apiName, timestamp, notification));
            return Task.FromResult(Result(apiName, changed: apiName == "ACH_1", cacheConfirmed: true, hostStatus: "captured"));
        });
        Achievement[] items =
        [
            Item("ACH_1", earned: true, timestamp: 111),
            Item("ACH_2", earned: false, timestamp: null),
            Item("ACH_3", earned: true, timestamp: 333),
        ];

        AchievementBatchSyncResult result = await service.SyncEarnedAsync(3751950, items);

        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(0, result.Failed);
        Assert.Equal(["ACH_1", "ACH_3"], calls.Select(call => call.ApiName));
        Assert.Equal([111, 333], calls.Select(call => call.Timestamp));
        Assert.All(calls, call => Assert.False(call.Notification));
    }

    [Fact]
    public async Task BatchSync_CountsUnconfirmedAndThrownWritesAsFailures()
    {
        var service = new AchievementSteamSyncService((_, apiName, _, _, _) =>
        {
            if (apiName == "ACH_THROW") throw new IOException("test failure");
            return Task.FromResult(Result(apiName, changed: true, cacheConfirmed: true, hostStatus: "unavailable"));
        });

        AchievementBatchSyncResult result = await service.SyncEarnedAsync(3751950,
        [
            Item("ACH_UNCONFIRMED", earned: true, timestamp: 1),
            Item("ACH_THROW", earned: true, timestamp: 2),
        ]);

        Assert.Equal(2, result.Failed);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.AlreadyPresent);
    }

    private static Achievement Item(string apiName, bool earned, long? timestamp) => new(
        apiName, apiName, "", null, null, earned, timestamp, "uplay_r2", false, null);

    private static LocalSteamSyncResult Result(
        string apiName,
        bool changed,
        bool cacheConfirmed,
        string hostStatus) => new(
            3751950,
            apiName,
            changed,
            1,
            0,
            2,
            123,
            hostStatus,
            cacheConfirmed,
            true,
            false,
            "not_requested",
            null,
            null);
}
