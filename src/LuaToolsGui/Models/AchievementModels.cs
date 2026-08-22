namespace LuaToolsGui.Models;

/// <summary>A provider-neutral achievement presented by LuaTools.</summary>
public sealed record Achievement(
    string ApiName,
    string DisplayName,
    string Description,
    string? IconUrl,
    string? LockedIconUrl,
    bool Earned,
    long? EarnedTime,
    string StateSource,
    bool Hidden,
    double? GlobalPercent);

/// <summary>A catalog assembled from Steam metadata plus any compatible local state provider.</summary>
public sealed record AchievementCatalog(
    long AppId,
    string MetadataSource,
    string? StatePath,
    bool StateFileExists,
    IReadOnlyList<Achievement> Achievements)
{
    public int EarnedCount => Achievements.Count(item => item.Earned);
}
