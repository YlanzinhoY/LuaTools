using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Steam-first catalog resolver with game-specific local providers as fallback/overlay.</summary>
public sealed class AchievementCatalogService(
    AchievementBridgeClient bridge,
    R2AchievementService r2)
{
    public async Task<AchievementCatalog> LoadAsync(long appId, CancellationToken cancellationToken = default)
    {
        AchievementCatalog? steam = null;
        Exception? steamError = null;
        try
        {
            steam = await bridge.LoadSteamCatalogAsync(appId, cancellationToken);
        }
        catch (Exception ex)
        {
            steamError = ex;
        }

        if (!R2AchievementService.Supports(appId))
            return steam ?? throw steamError ?? new InvalidOperationException("No achievement provider is available.");

        R2AchievementCatalog local = await Task.Run(() => r2.Load(appId), cancellationToken);
        if (steam is null || steam.Achievements.Count == 0) return FromR2(local);
        return MergeR2(steam, local);
    }

    private static AchievementCatalog FromR2(R2AchievementCatalog catalog) => new(
        catalog.SteamAppId,
        "uplay_r2",
        catalog.StatePath,
        catalog.StateFileExists,
        catalog.Achievements.Select(FromR2).ToList());

    internal static AchievementCatalog MergeR2(AchievementCatalog steam, R2AchievementCatalog r2)
    {
        Dictionary<string, R2Achievement> local = r2.Achievements.ToDictionary(
            item => item.ApiName,
            StringComparer.OrdinalIgnoreCase);
        var merged = new List<Achievement>(steam.Achievements.Count);
        foreach (Achievement item in steam.Achievements)
        {
            if (!local.Remove(item.ApiName, out R2Achievement? r2Item))
            {
                merged.Add(item);
                continue;
            }
            bool promotedByR2 = !item.Earned && r2Item.Earned;
            merged.Add(item with
            {
                IconUrl = item.IconUrl ?? r2Item.IconUrl,
                Earned = item.Earned || r2Item.Earned,
                EarnedTime = item.Earned ? item.EarnedTime : r2Item.EarnedTime,
                StateSource = promotedByR2 ? "uplay_r2" : item.StateSource,
            });
        }
        merged.AddRange(local.Values.Select(FromR2));
        return steam with
        {
            MetadataSource = "steam_client+uplay_r2",
            StatePath = r2.StatePath,
            StateFileExists = r2.StateFileExists,
            Achievements = merged,
        };
    }

    private static Achievement FromR2(R2Achievement item) => new(
        item.ApiName,
        item.DisplayName,
        item.Description,
        item.IconUrl,
        null,
        item.Earned,
        item.EarnedTime,
        "uplay_r2",
        false,
        null);
}
