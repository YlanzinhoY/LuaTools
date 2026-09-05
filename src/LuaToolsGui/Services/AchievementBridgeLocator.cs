using System.IO;

namespace LuaToolsGui.Services;

/// <summary>
/// Locates the independently installed Achievement Bridge for explicit catalog
/// and sync actions. LuaTools never starts its monitor or owns its lifecycle.
/// </summary>
internal static class AchievementBridgeLocator
{
    internal static string? FindExecutable()
    {
        string? developmentOverride = Environment.GetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            developmentOverride ?? "",
            Path.Combine(localAppData, "YlanzinhoY.AchievementBridge", "current", "achievement-bridge.exe"),
        ];
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}
