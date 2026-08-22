using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using System.Windows.Media;

namespace LuaToolsGui.ViewModels;

/// <summary>Bindable achievement row whose icon arrives asynchronously from the Steam CDN cache.</summary>
public sealed partial class R2AchievementRowViewModel(R2Achievement achievement) : ObservableObject
{
    public int Id => achievement.Id;
    public string ApiName => achievement.ApiName;
    public string DisplayName => achievement.DisplayName;
    public string Description => achievement.Description;
    public string? IconUrl => achievement.IconUrl;
    public bool Earned => achievement.Earned;
    public long? EarnedTime => achievement.EarnedTime;

    [ObservableProperty] private ImageSource? _icon;
}

/// <summary>Read-only popup model for one game's local Ubisoft R2 achievements.</summary>
public partial class R2AchievementsViewModel(
    R2AchievementService achievements,
    AchievementIconService icons,
    AchievementPopupService popups) : ObservableObject
{
    private List<R2AchievementRowViewModel> _all = [];

    public ObservableCollection<R2AchievementRowViewModel> Items { get; } = [];
    public Action? CloseRequested { get; set; }

    [ObservableProperty] private long _appId;
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private int _productId;
    [ObservableProperty] private string _statePath = "";
    [ObservableProperty] private bool _stateFileExists;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowEmpty))] private bool _isLoading;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowEmpty))] private string? _error;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _earnedCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _totalCount;

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
            R2AchievementCatalog catalog = await Task.Run(() => achievements.Load(AppId));
            _all = catalog.Achievements.Select(item => new R2AchievementRowViewModel(item)).ToList();
            ProductId = catalog.ProductId;
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
        R2AchievementRowViewModel? row = _all.FirstOrDefault(item => item.Earned) ?? _all.FirstOrDefault();
        if (row is not null) await popups.ShowR2Async(ProductId, row.Id.ToString());
    }

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        IEnumerable<R2AchievementRowViewModel> filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.ApiName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));

        Items.Clear();
        foreach (R2AchievementRowViewModel item in filtered) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private async Task LoadIconsAsync(IReadOnlyList<R2AchievementRowViewModel> rows)
    {
        await Task.WhenAll(rows.Select(async row => row.Icon = await icons.GetAsync(row.IconUrl)));
    }
}
