using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Serializes every Steam-local achievement mutation, whether it came from a live provider event
/// or from the explicit achievements-page sync. A bulk sync never requests toasts: historical
/// achievements are reflected in local state without flooding the player with notifications.
/// </summary>
public sealed class AchievementSteamSyncService
{
    private readonly Func<long, string, long, bool, CancellationToken, Task<LocalSteamSyncResult>> _syncOne;
    private readonly SemaphoreSlim _queue = new(1, 1);

    public AchievementSteamSyncService(AchievementBridgeClient bridge)
        : this(bridge.SyncLocalAchievementAsync) { }

    internal AchievementSteamSyncService(
        Func<long, string, long, bool, CancellationToken, Task<LocalSteamSyncResult>> syncOne) =>
        _syncOne = syncOne;

    public async Task<LocalSteamSyncResult> SyncOneAsync(
        long appId,
        string apiName,
        long unlockTime,
        bool experimentalSteamNotification,
        CancellationToken cancellationToken = default)
    {
        await _queue.WaitAsync(cancellationToken);
        try
        {
            return await _syncOne(
                appId,
                apiName,
                unlockTime,
                experimentalSteamNotification,
                cancellationToken);
        }
        finally
        {
            _queue.Release();
        }
    }

    public async Task<AchievementBatchSyncResult> SyncEarnedAsync(
        long appId,
        IReadOnlyList<Achievement> achievements,
        IProgress<AchievementBatchSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Achievement[] earned = achievements.Where(item => item.Earned).ToArray();
        int updated = 0;
        int alreadyPresent = 0;
        int failed = 0;
        long? accountId = null;
        List<string> confirmedApiNames = [];
        long fallbackTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        for (int index = 0; index < earned.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Achievement item = earned[index];
            progress?.Report(new AchievementBatchSyncProgress(index + 1, earned.Length, item.DisplayName));
            try
            {
                LocalSteamSyncResult result = await SyncOneAsync(
                    appId,
                    item.ApiName,
                    item.EarnedTime is > 0 ? item.EarnedTime.Value : fallbackTimestamp,
                    experimentalSteamNotification: false,
                    cancellationToken);
                if (!IsDurableLocalResult(result))
                    failed++;
                else
                {
                    accountId ??= result.AccountId;
                    if (accountId != result.AccountId)
                    {
                        failed++;
                        continue;
                    }
                    confirmedApiNames.Add(item.ApiName);
                    if (result.Changed)
                        updated++;
                    else
                        alreadyPresent++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed++;
            }
        }

        return new AchievementBatchSyncResult(
            earned.Length,
            updated,
            alreadyPresent,
            failed,
            accountId,
            confirmedApiNames);
    }

    internal static bool IsDurableLocalResult(LocalSteamSyncResult result) =>
        result.CacheConfirmed &&
        (result.HostStatus.Equals("captured", StringComparison.OrdinalIgnoreCase) || result.SteamConfirmed);
}

public sealed record AchievementBatchSyncProgress(int Current, int Total, string DisplayName);

public sealed record AchievementBatchSyncResult(
    int Total,
    int Updated,
    int AlreadyPresent,
    int Failed,
    long? AccountId,
    IReadOnlyList<string> ConfirmedApiNames);
