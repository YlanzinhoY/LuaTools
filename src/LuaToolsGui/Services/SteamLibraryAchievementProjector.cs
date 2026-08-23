using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Projects bridge-confirmed local unlocks into Steam Desktop's library summary. Protected
/// achievements can exist in UserGameStats while Steam still renders them as locked; this cache
/// is the final, desktop-only presentation layer. Steam must be stopped so it cannot overwrite the
/// atomic replacement with its stale in-memory copy.
/// </summary>
public sealed class SteamLibraryAchievementProjector
{
    private readonly Func<string?> _steamRoot;
    private readonly Func<bool> _steamRunning;

    public SteamLibraryAchievementProjector(SteamService steam)
        : this(() => steam.EffectivePath, SteamService.IsSteamRunning) { }

    internal SteamLibraryAchievementProjector(Func<string?> steamRoot, Func<bool> steamRunning)
    {
        _steamRoot = steamRoot;
        _steamRunning = steamRunning;
    }

    public AchievementLibraryProjectionResult Project(
        long appId,
        long accountId,
        IReadOnlyList<Achievement> catalog,
        IReadOnlySet<string> confirmedApiNames)
    {
        if (_steamRunning())
            throw new InvalidOperationException("Steam must be stopped before updating its achievement library cache.");
        string root = _steamRoot()
            ?? throw new DirectoryNotFoundException("Steam is not installed or configured.");
        string cachePath = Path.Combine(root, "userdata", accountId.ToString(), "config", "librarycache", $"{appId}.json");
        if (!File.Exists(cachePath))
            throw new FileNotFoundException("Steam has not created the game's library cache yet.", cachePath);

        JsonArray document = JsonNode.Parse(File.ReadAllText(cachePath, Encoding.UTF8)) as JsonArray
            ?? throw new InvalidDataException("Steam returned an invalid library cache document.");
        JsonObject data = FindAchievementData(document)
            ?? throw new InvalidDataException("Steam's library cache has no achievements section.");

        var existing = ExistingRows(data);
        long fallbackTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Achievement[] confirmed = catalog
            .Where(item => item.Earned && confirmedApiNames.Contains(item.ApiName))
            .ToArray();

        JsonArray highlights = new(confirmed
            .OrderByDescending(item => item.EarnedTime ?? fallbackTime)
            .Take(12)
            .Select(item => (JsonNode?)CreateRow(item, achieved: true, item.EarnedTime ?? fallbackTime, existing))
            .ToArray());
        HashSet<string> achievedIds = confirmed.Select(item => item.ApiName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        JsonArray unachieved = new(catalog
            .Where(item => !achievedIds.Contains(item.ApiName))
            .OrderByDescending(item => item.GlobalPercent ?? -1)
            .Take(12)
            .Select(item => (JsonNode?)CreateRow(item, achieved: false, 0, existing))
            .ToArray());

        data["vecHighlight"] = highlights;
        data["vecUnachieved"] = unachieved;
        data["vecAchievedHidden"] ??= new JsonArray();
        data["nTotal"] = catalog.Count;
        data["nAchieved"] = confirmed.Length;

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        string backupPath = $"{cachePath}.pre-achievement-bridge-{stamp}.bak";
        File.Copy(cachePath, backupPath, overwrite: false);
        string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }

        JsonArray persisted = JsonNode.Parse(File.ReadAllText(cachePath, Encoding.UTF8)) as JsonArray
            ?? throw new InvalidDataException("Steam's updated library cache could not be verified.");
        JsonObject verified = FindAchievementData(persisted)
            ?? throw new InvalidDataException("Steam's updated library cache lost its achievements section.");
        int persistedCount = verified["nAchieved"]?.GetValue<int>() ?? -1;
        if (persistedCount != confirmed.Length)
            throw new IOException("Steam's updated achievement count could not be verified.");

        return new AchievementLibraryProjectionResult(cachePath, backupPath, confirmed.Length, highlights.Count);
    }

    private static JsonObject? FindAchievementData(JsonArray document)
    {
        foreach (JsonNode? node in document)
        {
            if (node is not JsonArray entry || entry.Count < 2 || entry[0]?.GetValue<string>() != "achievements") continue;
            return entry[1]?["data"] as JsonObject;
        }
        return null;
    }

    private static Dictionary<string, JsonObject> ExistingRows(JsonObject data)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (string vector in new[] { "vecHighlight", "vecUnachieved", "vecAchievedHidden" })
        {
            if (data[vector] is not JsonArray items) continue;
            foreach (JsonNode? node in items)
            {
                if (node is not JsonObject item || item["strID"]?.GetValue<string>() is not { Length: > 0 } id) continue;
                rows.TryAdd(id, item);
            }
        }
        return rows;
    }

    private static JsonObject CreateRow(
        Achievement achievement,
        bool achieved,
        long unlockTime,
        IReadOnlyDictionary<string, JsonObject> existing)
    {
        JsonObject row = existing.TryGetValue(achievement.ApiName, out JsonObject? old)
            ? (JsonObject)old.DeepClone()
            : new JsonObject();
        row["strID"] = achievement.ApiName;
        row["strName"] = achievement.DisplayName;
        row["strDescription"] = achievement.Description;
        row["bAchieved"] = achieved;
        row["rtUnlocked"] = achieved ? unlockTime : 0;
        if (!string.IsNullOrWhiteSpace(achievement.IconUrl)) row["strImage"] = achievement.IconUrl;
        row["bHidden"] = achievement.Hidden;
        row["flMinProgress"] = 0;
        row["flCurrentProgress"] = 0;
        row["flMaxProgress"] = 0;
        if (achievement.GlobalPercent is { } percent) row["flAchieved"] = percent;
        return row;
    }
}

public sealed record AchievementLibraryProjectionResult(
    string CachePath,
    string BackupPath,
    int AchievedCount,
    int HighlightCount);
