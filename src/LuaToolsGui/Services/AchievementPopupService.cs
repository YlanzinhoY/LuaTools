using System.Media;
using System.Windows;
using System.Windows.Media;
using LuaToolsGui.Models;
using LuaToolsGui.Views;

namespace LuaToolsGui.Services;

/// <summary>Shows image-rich unlock notifications above the game without activating LuaTools.</summary>
public sealed class AchievementPopupService(AchievementIconService icons)
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
