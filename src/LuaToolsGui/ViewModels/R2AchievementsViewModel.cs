using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>Read-only popup model for one game's local Ubisoft R2 achievements.</summary>
public partial class R2AchievementsViewModel(R2AchievementService achievements) : ObservableObject
{
    private List<R2Achievement> _all = [];

    public ObservableCollection<R2Achievement> Items { get; } = [];
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
            _all = [.. catalog.Achievements];
            ProductId = catalog.ProductId;
            StatePath = catalog.StatePath;
            StateFileExists = catalog.StateFileExists;
            EarnedCount = catalog.EarnedCount;
            TotalCount = catalog.Achievements.Count;
            ApplyFilter();
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

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        IEnumerable<R2Achievement> filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.ApiName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));

        Items.Clear();
        foreach (R2Achievement item in filtered) Items.Add(item);
        OnPropertyChanged(nameof(ShowEmpty));
    }
}
