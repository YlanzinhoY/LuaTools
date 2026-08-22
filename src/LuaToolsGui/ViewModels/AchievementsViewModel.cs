using System.Collections.ObjectModel;
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
    AchievementPopupService popups) : ObservableObject
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
            ApplyFilter();
            _ = LoadIconsAsync(_all);
        }
        catch (Exception ex)
        {
            _all = [];
            Items.Clear();
            EarnedCount = 0;
            TotalCount = 0;
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
