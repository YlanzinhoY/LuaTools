using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

public partial class AchievementBridgeLogViewModel : ObservableObject
{
    private readonly AchievementBridgeLogService _logs;

    public AchievementBridgeLogViewModel(AchievementBridgeLogService logs)
    {
        _logs = logs;
        foreach (AchievementBridgeLogEntry entry in logs.Snapshot()) Entries.Add(entry);
        _logs.EntryAdded += OnEntryAdded;
        _logs.StateChanged += OnStateChanged;
    }

    public ObservableCollection<AchievementBridgeLogEntry> Entries { get; } = [];
    public bool IsRunning => _logs.IsRunning;
    public bool HasEntries => Entries.Count > 0;
    public string StatusText => IsRunning
        ? Resources.Strings.BridgeLogs_Status_Running
        : Resources.Strings.BridgeLogs_Status_Stopped;
    public string ProcessText => _logs.ProcessId is int pid
        ? string.Format(Resources.Strings.BridgeLogs_Process, pid)
        : Resources.Strings.BridgeLogs_Process_None;
    public string ActiveGamesText => string.Format(Resources.Strings.BridgeLogs_ActiveGames, _logs.ActiveGameCount);
    public string LogPath => _logs.LogPath;

    [RelayCommand]
    private void Clear()
    {
        _logs.Clear();
        Entries.Clear();
        OnPropertyChanged(nameof(HasEntries));
    }

    [RelayCommand]
    private void OpenFolder() => _logs.OpenLogFolder();

    private void OnEntryAdded(AchievementBridgeLogEntry entry) => Dispatch(() =>
    {
        Entries.Add(entry);
        while (Entries.Count > 1000) Entries.RemoveAt(0);
        OnPropertyChanged(nameof(HasEntries));
    });

    private void OnStateChanged() => Dispatch(() =>
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProcessText));
        OnPropertyChanged(nameof(ActiveGamesText));
        OnPropertyChanged(nameof(HasEntries));
    });

    private static void Dispatch(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}
