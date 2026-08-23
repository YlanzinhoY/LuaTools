using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>Bindable provider-neutral achievement row whose icon is loaded asynchronously.</summary>
public sealed partial class AchievementRowViewModel(Achievement achievement) : ObservableObject
{
    public Achievement Achievement => achievement;
    public string ApiName => achievement.ApiName;
    public string DisplayName => achievement.DisplayName;
    public string Description => achievement.Description;
    // Keep each achievement recognizable while locked; the row already dims the artwork and adds
    // a lock badge. Steam often uses one generic icon_gray for every locked item in a game.
    public string? IconUrl => achievement.IconUrl ?? achievement.LockedIconUrl;
    public bool Earned => achievement.Earned;
    public long? EarnedTime => achievement.EarnedTime;
    public string StateSource => achievement.StateSource;
    public double? GlobalPercent => achievement.GlobalPercent;

    [ObservableProperty] private ImageSource? _icon;
}

/// <summary>Read-only achievement catalog for any Steam AppID supported by the bridge.</summary>
public partial class AchievementsViewModel(
    AchievementCatalogService achievements,
    AchievementIconService icons,
    AchievementPopupService popups,
    R2AchievementService r2,
    AchievementSteamSyncService steamSync,
    SteamLibraryAchievementProjector libraryProjector,
    SteamService steam) : ObservableObject
{
    private List<AchievementRowViewModel> _all = [];

    public ObservableCollection<AchievementRowViewModel> Items { get; } = [];
    public Action? CloseRequested { get; set; }

    [ObservableProperty] private long _appId;
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private string _sourceLabel = "Steam";
    [ObservableProperty] private string? _statePath;
    [ObservableProperty] private bool _stateFileExists;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowEmpty))] private bool _isLoading;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowEmpty))] private string? _error;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ProgressText))] private int _earnedCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ProgressText))] private int _totalCount;
    [ObservableProperty] private bool _supportsSteamSync;
    [ObservableProperty] private bool _canSyncToSteam;
    [ObservableProperty] private bool _isSyncingToSteam;
    [ObservableProperty] private string? _syncStatus;

    public string ProgressText =>
        string.Format(Resources.Strings.Achievements_Progress, EarnedCount, TotalCount);

    public bool ShowEmpty => !IsLoading && Error is null && Items.Count == 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task LoadAsync(long appId, string gameName)
    {
        AppId = appId;
        GameName = gameName;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        Error = null;
        try
        {
            AchievementCatalog catalog = await achievements.LoadAsync(AppId);
            _all = catalog.Achievements.Select(item => new AchievementRowViewModel(item)).ToList();
            SourceLabel = FriendlySource(catalog.MetadataSource);
            StatePath = catalog.StatePath;
            StateFileExists = catalog.StateFileExists;
            EarnedCount = catalog.EarnedCount;
            TotalCount = catalog.Achievements.Count;
            SupportsSteamSync = R2AchievementService.Supports(AppId);
            CanSyncToSteam = SupportsSteamSync && catalog.StateFileExists && EarnedCount > 0 && !IsSyncingToSteam;
            ApplyFilter();
            _ = LoadIconsAsync(_all);
        }
        catch (Exception ex)
        {
            _all = [];
            Items.Clear();
            EarnedCount = 0;
            TotalCount = 0;
            CanSyncToSteam = false;
            Error = string.Format(Resources.Strings.Achievements_LoadFailed, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task PreviewAsync()
    {
        AchievementRowViewModel? row = _all.FirstOrDefault(item => item.Earned) ?? _all.FirstOrDefault();
        if (row is not null) await popups.ShowAchievementAsync(row.Achievement);
    }

    [RelayCommand]
    private async Task SyncToSteamAsync()
    {
        if (IsSyncingToSteam || !SupportsSteamSync) return;

        IsSyncingToSteam = true;
        CanSyncToSteam = false;
        SyncStatus = null;
        try
        {
            R2AchievementCatalog local = await Task.Run(() => r2.Load(AppId));
            List<Achievement> catalog = local.Achievements
                .Select(item => new Achievement(
                    item.ApiName,
                    item.DisplayName,
                    item.Description,
                    item.IconUrl,
                    null,
                    true,
                    item.EarnedTime,
                    "uplay_r2",
                    false,
                    null))
                .ToList();
            List<Achievement> earned = catalog.Where(item => item.Earned).ToList();
            if (earned.Count == 0)
            {
                SyncStatus = Resources.Strings.Achievements_SyncNone;
                return;
            }

            var progress = new Progress<AchievementBatchSyncProgress>(item =>
                SyncStatus = string.Format(
                    Resources.Strings.Achievements_SyncProgress,
                    item.Current,
                    item.Total,
                    item.DisplayName));
            AchievementBatchSyncResult result = await steamSync.SyncEarnedAsync(AppId, catalog, progress);
            if (result.ConfirmedApiNames.Count > 0)
            {
                if (result.AccountId is not { } accountId)
                    throw new InvalidOperationException("Achievement Bridge did not identify the active Steam account.");
                SyncStatus = Resources.Strings.Achievements_SyncRestarting;
                if (!await steam.StopSteamGracefullyAsync())
                    throw new IOException("Steam did not close normally. Close any running game and try again.");
                try
                {
                    libraryProjector.Project(
                        AppId,
                        accountId,
                        catalog,
                        result.ConfirmedApiNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
                }
                finally
                {
                    steam.StartSteam();
                }
                await Task.Delay(TimeSpan.FromSeconds(4));
                SteamService.OpenUrl($"steam://nav/games/details/{AppId}");
            }
            SyncStatus = string.Format(
                Resources.Strings.Achievements_SyncSummary,
                result.Updated,
                result.AlreadyPresent,
                result.Failed);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SyncStatus = string.Format(Resources.Strings.Achievements_SyncFailed, ex.Message);
        }
        finally
        {
            IsSyncingToSteam = false;
            CanSyncToSteam = SupportsSteamSync && StateFileExists && EarnedCount > 0;
        }
    }

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        IEnumerable<AchievementRowViewModel> filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.ApiName.Contains(query, StringComparison.OrdinalIgnoreCase));

        Items.Clear();
        foreach (AchievementRowViewModel item in filtered) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private async Task LoadIconsAsync(IReadOnlyList<AchievementRowViewModel> rows)
    {
        await Task.WhenAll(rows.Select(async row => row.Icon = await icons.GetAsync(row.IconUrl)));
    }

    private static string FriendlySource(string source) => source switch
    {
        "steam_client" => "Steam",
        "uplay_r2" => "Ubisoft R2",
        "steam_client+uplay_r2" => "Steam + Ubisoft R2",
        _ => source,
    };
}
