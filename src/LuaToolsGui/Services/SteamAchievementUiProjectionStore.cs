using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Persists desktop-only achievement projections consumed by the CEF injector. Steam replaces the
/// librarycache copy of protected achievements with its server response, so the durable presentation
/// layer must be applied after each React render instead of racing that file write.
/// </summary>
public sealed class SteamAchievementUiProjectionStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<long, Projection> _projections;

    public SteamAchievementUiProjectionStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LuaToolsGui",
        "steam-achievement-projections.json")) { }

    internal SteamAchievementUiProjectionStore(string path)
    {
        _path = path;
        _projections = Load(path);
    }

    public void Save(
        long appId,
        IReadOnlyList<Achievement> catalog,
        IReadOnlySet<string> confirmedApiNames)
    {
        Projection projection = new(
            appId,
            catalog.Count,
            catalog.Count(item => item.Earned && confirmedApiNames.Contains(item.ApiName)),
            catalog
                .Where(item => item.Earned && confirmedApiNames.Contains(item.ApiName))
                .OrderByDescending(item => item.EarnedTime ?? 0)
                .Select(item => new ProjectionAchievement(
                    item.ApiName,
                    item.DisplayName,
                    item.Description,
                    item.IconUrl,
                    item.EarnedTime ?? 0))
                .ToArray());

        lock (_gate)
        {
            _projections[appId] = projection;
            WriteAtomic(_path, _projections.Values.OrderBy(item => item.AppId).ToArray());
        }
    }

    public string BuildPatchScript()
    {
        Projection[] projections;
        lock (_gate) projections = _projections.Values.ToArray();
        if (projections.Length == 0) return "";
        string json = JsonSerializer.Serialize(projections);
        return $$"""
            (() => {
              const projections = {{json}};
              for (const p of projections) {
                const token = `/apps/${p.app_id}/`;
                const appImages = [...document.images].filter(img => (img.src || '').includes(token));
                if (!appImages.length) continue;
                const section = appImages
                  .map(img => img.closest('[class~="AppDetailsSection"]'))
                  .find(node => node && node.querySelector('[class*="AchievementProgress"]'));
                if (!section) continue;

                const ratio = `${p.achieved_count}/${p.total_count}`;
                section.querySelectorAll('[class*="UnlockedLabel"] span:first-child').forEach(node => {
                  node.textContent = (node.textContent || '').replace(/^\s*\d+\/\d+/, ` ${ratio}`);
                });
                section.querySelectorAll('[class*="UnlockedLabelPercent"]').forEach(node => {
                  node.textContent = ` (${Math.round(p.achieved_count * 100 / Math.max(1, p.total_count))}%)`;
                });
                section.querySelectorAll('[class~="AchievementProgress"]').forEach(node => {
                  node.style.width = `${p.achieved_count * 100 / Math.max(1, p.total_count)}%`;
                });

                const cards = [...section.querySelectorAll('[class*="AdditionalItem"]')];
                cards.forEach((card, index) => {
                  const achievement = p.achievements[index];
                  if (!achievement) return;
                  card.classList.remove('NotAchieved');
                  card.classList.add('Achieved');
                  card.dataset.achievementBridge = achievement.api_name;
                  card.title = `${achievement.name}\n${achievement.description || ''}`;
                  const image = card.querySelector('img');
                  if (image && achievement.icon_url) image.src = achievement.icon_url;
                  if (image) { image.alt = achievement.name; image.style.filter = 'none'; image.style.opacity = '1'; }
                });

                const exactRatio = new RegExp(`^\\s*\\d+/${p.total_count}\\s*$`);
                [...document.querySelectorAll('div,span')]
                  .filter(node => node.children.length === 0 && exactRatio.test(node.textContent || ''))
                  .forEach(node => { node.textContent = ratio; });
                section.dataset.achievementBridgeProjection = String(p.achieved_count);
              }
            })();
            """;
    }

    private static Dictionary<long, Projection> Load(string path)
    {
        try
        {
            Projection[] items = JsonSerializer.Deserialize<Projection[]>(File.ReadAllText(path, Encoding.UTF8)) ?? [];
            return items.ToDictionary(item => item.AppId);
        }
        catch
        {
            return [];
        }
    }

    private static void WriteAtomic(string path, Projection[] projections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(projections), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal sealed record Projection(
        [property: JsonPropertyName("app_id")] long AppId,
        [property: JsonPropertyName("total_count")] int TotalCount,
        [property: JsonPropertyName("achieved_count")] int AchievedCount,
        [property: JsonPropertyName("achievements")] ProjectionAchievement[] Achievements);

    internal sealed record ProjectionAchievement(
        [property: JsonPropertyName("api_name")] string ApiName,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("icon_url")] string? IconUrl,
        [property: JsonPropertyName("unlocked_at")] long UnlockedAt);
}
