using System.Media;
using System.Windows;
using System.Windows.Media;
using LuaToolsGui.Models;
using LuaToolsGui.Views;

namespace LuaToolsGui.Services;

/// <summary>Shows image-rich unlock notifications above the game without activating LuaTools.</summary>
public sealed class AchievementPopupService(
    AchievementCatalogService achievements,
    AchievementIconService icons)
{
    private readonly SemaphoreSlim _queue = new(1, 1);

    public async Task ShowAchievementAsync(Achievement achievement)
    {
        ImageSource? icon = await icons.GetAsync(achievement.IconUrl);
        await ShowAsync(new AchievementPopupContent(
            Resources.Strings.Achievements_NotificationTitle,
            achievement.DisplayName,
            achievement.Description,
            icon));
    }

    internal async Task ShowBridgeEventAsync(AchievementBridgeEvent achievement)
    {
        long? appId = achievement.AppId;
        if (appId is null && achievement.Provider.Equals("uplay_r2", StringComparison.OrdinalIgnoreCase) &&
            achievement.ProductId == R2AchievementService.BlackFlagProductId)
            appId = R2AchievementService.BlackFlagSteamAppId;
        if (appId is null) return;

        try
        {
            AchievementCatalog catalog = await achievements.LoadAsync(appId.Value);
            Achievement? item = catalog.Achievements.FirstOrDefault(candidate =>
                candidate.ApiName.Equals(achievement.Achievement, StringComparison.OrdinalIgnoreCase))
                ?? catalog.Achievements.FirstOrDefault(candidate =>
                    NumericSuffix(candidate.ApiName) == achievement.Achievement);
            if (item is not null) await ShowAchievementAsync(item);
        }
        catch
        {
            // A notification is best-effort and must never terminate a provider reader.
        }
    }

    private static string? NumericSuffix(string apiName)
    {
        int start = apiName.Length;
        while (start > 0 && char.IsDigit(apiName[start - 1])) start--;
        return start == apiName.Length ? null : apiName[start..];
    }

    private async Task ShowAsync(AchievementPopupContent content)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        await _queue.WaitAsync();
        try
        {
            await dispatcher.InvokeAsync(() => ShowWindowAsync(content)).Task.Unwrap();
        }
        finally
        {
            _queue.Release();
        }
    }

    private static async Task ShowWindowAsync(AchievementPopupContent content)
    {
        var popup = new AchievementUnlockedWindow(content);
        Rect workArea = SystemParameters.WorkArea;
        popup.Left = workArea.Right - popup.Width - 24;
        popup.Top = workArea.Bottom - popup.Height - 24;
        popup.Show();
        SystemSounds.Asterisk.Play();

        await Task.Delay(TimeSpan.FromSeconds(6));
        if (popup.IsLoaded) popup.Close();
    }
}

public sealed record AchievementPopupContent(
    string Header,
    string Name,
    string Description,
    ImageSource? Icon);
