namespace LuaToolsGui.Models;

/// <summary>One Ubisoft Connect R2 achievement as exposed by the local emulator state.</summary>
public sealed record R2Achievement(
    int Id,
    string ApiName,
    string DisplayName,
    string Description,
    string? IconUrl,
    bool Earned,
    long? EarnedTime);

/// <summary>A resolved catalog plus the R2 state file that supplied its unlocked flags.</summary>
public sealed record R2AchievementCatalog(
    long SteamAppId,
    int ProductId,
    string StatePath,
    bool StateFileExists,
    IReadOnlyList<R2Achievement> Achievements)
{
    public int EarnedCount => Achievements.Count(a => a.Earned);
}
