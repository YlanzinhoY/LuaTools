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
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool migrated = false;
        foreach ((long appId, Projection projection) in _projections.ToArray())
        {
            ProjectionAchievement[] achievements = projection.Achievements
                .Select(item => item.UnlockedAt > 0 ? item : item with { UnlockedAt = now })
                .ToArray();
            if (!achievements.SequenceEqual(projection.Achievements))
            {
                _projections[appId] = projection with { Achievements = achievements };
                migrated = true;
            }
        }
        if (migrated) WriteAtomic(_path, _projections.Values.OrderBy(item => item.AppId).ToArray());
    }

    public void Save(
        long appId,
        IReadOnlyList<Achievement> catalog,
        IReadOnlySet<string> confirmedApiNames)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
                    item.EarnedTime is > 0 ? item.EarnedTime.Value : now))
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
              const percentage = p => Math.round(p.achieved_count * 100 / Math.max(1, p.total_count));
              const achievementFromRow = row => {
                const fiberKey = Object.keys(row).find(key => key.startsWith('__reactFiber$'));
                let fiber = fiberKey ? row[fiberKey] : null;
                for (let depth = 0; fiber && depth < 12; depth++, fiber = fiber.return) {
                  const achievement = fiber.memoizedProps && fiber.memoizedProps.achievement;
                  if (achievement && achievement.strID) return achievement;
                }
                return null;
              };
              const element = (tag, className, text) => {
                const node = document.createElement(tag);
                node.className = className;
                if (text != null) node.textContent = text;
                return node;
              };
              const localeText = (kind, value) => {
                const language = new URL(location.href).searchParams.get('LANGUAGE') || '';
                const portuguese = /brazilian|portuguese|português|^pt/i.test(language);
                if (kind === 'global') return portuguese
                  ? `Alcançada por ${value}% dos jogadores`
                  : `Achieved by ${value}% of players`;
                return portuguese ? `Alcançada em ${value}` : `Unlocked ${value}`;
              };
              const formatDate = unixTime => new Intl.DateTimeFormat(undefined, {
                day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit'
              }).format(new Date(unixTime * 1000));
              const renderUnlockedRow = (row, achievement, steamAchievement) => {
                row.dataset.achievementBridgeUnlocked = achievement.api_name;
                const image = row.querySelector('img');
                if (image && achievement.icon_url) image.src = achievement.icon_url;
                if (image) {
                  image.alt = achievement.name;
                  image.style.filter = 'none';
                  image.style.opacity = '1';
                }

                const contentHost = row.querySelector('[class~="Content"]');
                if (contentHost) {
                  contentHost.classList.remove('Hidden');
                  contentHost.replaceChildren();
                  const achievementContent = element('div',
                    'mW5BYTa-bB3M5rll1WiJ9 VerticalContent mvXQPo2h9znbzUIQ5M_pk AchievementContent');
                  const nativeName = steamAchievement && steamAchievement.strName && steamAchievement.strName !== '?'
                    ? steamAchievement.strName : achievement.name;
                  const nativeDescription = steamAchievement && steamAchievement.strDescription &&
                    !/oculta|hidden|revelad/i.test(steamAchievement.strDescription)
                    ? steamAchievement.strDescription : achievement.description;
                  achievementContent.append(
                    element('div', '_17dhGq-FsL8xZBes7dOOlN AchievementTitle', nativeName),
                    element('div', '_1WP2YDDWrL5LPHoS04AKx8 AchievementDescription', nativeDescription || '')
                  );
                  if (steamAchievement && Number.isFinite(steamAchievement.flAchieved)) {
                    achievementContent.append(element('div',
                      'VQbek1VTyOX04qHmTsssW AchievementGlobalPercentage _2IpxMHGN9qy7CjHVQQ9vy InBody',
                      localeText('global', Number(steamAchievement.flAchieved).toFixed(1))));
                  }
                  contentHost.append(achievementContent);
                }

                const right = row.querySelector('[class~="Right"]');
                if (right) {
                  right.replaceChildren();
                  const vertical = element('div',
                    'mW5BYTa-bB3M5rll1WiJ9 VerticalContent _33MZ93kqwhKeAWH7uGTR8 AlignEnd');
                  vertical.append(element('div', '_2fFW2raBfeDt3WOPB-aQAq UnlockDate',
                    localeText('date', formatDate(achievement.unlocked_at))));
                  right.append(vertical);
                }
              };
              const customAchievementRow = achievement => {
                const row = element('div', '_2Kmn7fJOkLT4KyWl467a9M AchievementListItemBase Panel');
                row.tabIndex = 0;
                row.dataset.achievementBridgeHidden = achievement.api_name;
                const container = element('div', 'cvw8DHMUBKuZs5DeLJOQ2 Container');
                const imageColumn = element('div', '');
                const wrapper = element('div', '_1fEbX-PfpZ2FhkhttWcm-V AchievementIconWrapper');
                wrapper.append(element('img', '_2V2sHETNfa62yMoDwSF3_t Icon'));
                imageColumn.append(wrapper);
                container.append(
                  imageColumn,
                  element('div', 'UWkKthLfz_5MM3NdASE2W Content'),
                  element('div', 'k02sevxj1a1YpBto7_V57 Right')
                );
                row.append(container);
                return row;
              };

              for (const p of projections) {
                const token = `/apps/${p.app_id}/`;
                const appImages = [...document.images].filter(img => (img.src || '').includes(token));
                const section = appImages
                  .map(img => img.closest('[class~="AppDetailsSection"]'))
                  .find(node => node && node.querySelector('[class*="AchievementProgress"]'));
                const ratio = `${p.achieved_count}/${p.total_count}`;
                if (section) {
                  section.querySelectorAll('[class*="UnlockedLabel"] span:first-child').forEach(node => {
                    node.textContent = (node.textContent || '').replace(/^\s*\d+\/\d+/, ` ${ratio}`);
                  });
                  section.querySelectorAll('[class*="UnlockedLabelPercent"]').forEach(node => {
                    node.textContent = ` (${percentage(p)}%)`;
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

                const playBarImage = [...document.images].find(img => {
                  const src = img.src || '';
                  const status = img.closest('[class~="StatusAndStats"]');
                  return src.includes(`/assets/${p.app_id}/`) &&
                    status && status.querySelector('[class~="MiniAchievements"]');
                });
                const miniAchievements = playBarImage
                  ?.closest('[class~="StatusAndStats"]')
                  ?.querySelector('[class~="MiniAchievements"]');
                if (miniAchievements) {
                  const progressContainer = miniAchievements.querySelector('[role="progressbar"]');
                  const progressBar = miniAchievements.querySelector('[class~="DetailsProgressBar"]');
                  if (progressContainer) {
                    progressContainer.setAttribute('aria-valuenow', String(percentage(p)));
                    progressContainer.setAttribute('aria-valuemin', '0');
                    progressContainer.setAttribute('aria-valuemax', '100');
                  }
                  if (progressBar) progressBar.style.width = `${percentage(p)}%`;
                  miniAchievements.dataset.achievementBridgeProjection = String(p.achieved_count);
                }

                const overlay = [...document.querySelectorAll('[class~="AchievementsOverlayContainer"]')]
                  .find(node => [...node.querySelectorAll('img')].some(img => {
                    const src = img.src || '';
                    return src.includes(token) || src.includes(`/assets/${p.app_id}/`);
                  }));
                if (!overlay) continue;

                const progressLabels = overlay.querySelectorAll('[class~="ProgressLabel"] [class~="Label"]');
                if (progressLabels[0]) {
                  progressLabels[0].textContent = (progressLabels[0].textContent || '')
                    .replace(/^\s*\d+/, String(p.achieved_count));
                }
                if (progressLabels[1]) progressLabels[1].textContent = `(${percentage(p)}%)`;

                const achievementMap = new Map(p.achievements.map(item => [item.api_name, item]));
                const myTab = [...overlay.querySelectorAll('[role="tabpanel"]')]
                  .find(node => /achievements_Content$/.test(node.id || ''));
                if (!myTab) continue;
                const listTitle = myTab.querySelector('[class~="ListTitle"]');
                if (listTitle) {
                  const language = new URL(location.href).searchParams.get('LANGUAGE') || '';
                  listTitle.textContent = /brazilian|portuguese|português|^pt/i.test(language)
                    ? 'Conquistas' : 'Achievements';
                }

                const firstNativeRow = [...myTab.querySelectorAll('[class~="AchievementListItemBase"]')]
                  .find(row => achievementFromRow(row));
                const knownSteamIds = new Set();
                if (firstNativeRow) {
                  const fiberKey = Object.keys(firstNativeRow).find(key => key.startsWith('__reactFiber$'));
                  let fiber = fiberKey ? firstNativeRow[fiberKey] : null;
                  for (let depth = 0; fiber && depth < 16; depth++, fiber = fiber.return) {
                    const props = fiber.memoizedProps;
                    if (!Array.isArray(props && props.rgUnachieved)) continue;
                    for (const steamAchievement of [...(props.rgAchieved || []), ...props.rgUnachieved]) {
                      if (!steamAchievement || !steamAchievement.strID) continue;
                      knownSteamIds.add(steamAchievement.strID);
                      const achievement = achievementMap.get(steamAchievement.strID);
                      if (!achievement) continue;
                      steamAchievement.bAchieved = true;
                      steamAchievement.rtUnlocked = achievement.unlocked_at;
                      if (achievement.icon_url) steamAchievement.strImage = achievement.icon_url;
                    }
                    break;
                  }
                }

                myTab.querySelectorAll('[class~="AchievementListItemBase"]').forEach(row => {
                  const steamAchievement = achievementFromRow(row);
                  const achievement = steamAchievement && achievementMap.get(steamAchievement.strID);
                  if (!achievement) return;

                  steamAchievement.bAchieved = true;
                  steamAchievement.rtUnlocked = achievement.unlocked_at;
                  if (achievement.icon_url) steamAchievement.strImage = achievement.icon_url;
                  renderUnlockedRow(row, achievement, steamAchievement);
                });

                const hiddenSummary = [...myTab.querySelectorAll('[class~="AchievementListItemBase"]')]
                  .find(row => row.querySelector('[class~="HiddenAchievementContent"]'));
                if (hiddenSummary && knownSteamIds.size) {
                  const hiddenAchievements = p.achievements.filter(item => !knownSteamIds.has(item.api_name));
                  for (const achievement of hiddenAchievements) {
                    let row = [...myTab.querySelectorAll('[data-achievement-bridge-hidden]')]
                      .find(node => node.dataset.achievementBridgeHidden === achievement.api_name);
                    if (!row) {
                      row = customAchievementRow(achievement);
                      hiddenSummary.before(row);
                    }
                    renderUnlockedRow(row, achievement, null);
                  }
                  const hiddenTitle = hiddenSummary.querySelector('[class~="AchievementTitle"]');
                  if (hiddenTitle) {
                    const currentCount = Number((hiddenTitle.textContent || '').match(/\d+/)?.[0]);
                    if (!hiddenSummary.dataset.achievementBridgeOriginalCount && Number.isFinite(currentCount))
                      hiddenSummary.dataset.achievementBridgeOriginalCount = String(currentCount);
                    const originalCount = Number(hiddenSummary.dataset.achievementBridgeOriginalCount || 0);
                    hiddenTitle.textContent = (hiddenTitle.textContent || '')
                      .replace(/\d+/, String(Math.max(0, originalCount - hiddenAchievements.length)));
                  }
                }
                overlay.dataset.achievementBridgeProjection = String(p.achieved_count);
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
