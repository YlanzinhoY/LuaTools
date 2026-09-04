using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

public partial class TorrentDownloadItemViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();

    public string Name { get; }
    public string Source { get; }
    public string DestinationFolder { get; }
    internal CancellationToken CancellationToken => _cancellation.Token;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = Resources.Strings.Add_Kazumi_State_Starting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isActive = true;

    private bool CanCancel => IsActive;

    internal TorrentDownloadItemViewModel(string name, string source, string destinationFolder)
    {
        Name = name;
        Source = source;
        DestinationFolder = destinationFolder;
    }

    internal void Report(TorrentDownloadProgress value)
    {
        if (!IsActive) return;
        Progress = value.Percent;
        StatusText = string.Format(
            Resources.Strings.Add_Kazumi_Progress,
            value.Percent,
            FormatBytes(value.DownloadRate),
            value.Peers,
            FormatState(value.State));
    }

    internal void MarkCompleted()
    {
        Progress = 100;
        StatusText = Resources.Strings.Add_Kazumi_Complete;
        IsActive = false;
    }

    internal void MarkCancelled()
    {
        StatusText = Resources.Strings.Add_Kazumi_Cancelled;
        IsActive = false;
    }

    internal void MarkFailed()
    {
        StatusText = Resources.Strings.Add_Kazumi_Err_Torrent;
        IsActive = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation.Cancel();

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(DestinationFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", DestinationFolder)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // The folder may have been removed or Explorer may be unavailable.
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatState(string state) => state switch
    {
        "Metadata" or "DownloadingMetadata" => Resources.Strings.Add_Kazumi_State_Metadata,
        "Starting" => Resources.Strings.Add_Kazumi_State_Starting,
        "Hashing" or "HashingPaused" or "CheckingFiles" or "CheckingResumeData" =>
            Resources.Strings.Add_Kazumi_State_Checking,
        "Downloading" => Resources.Strings.Add_Kazumi_State_Downloading,
        "Finished" or "Seeding" => Resources.Strings.Add_Kazumi_Complete,
        _ => state,
    };

    public void Dispose() => _cancellation.Dispose();
}

/// <summary>Application-wide queue and page view model for built-in Kazumi torrent downloads.</summary>
public partial class TorrentDownloadsViewModel : ObservableObject, IDisposable
{
    private readonly TorrentDownloadService _torrent;

    public ObservableCollection<TorrentDownloadItemViewModel> Downloads { get; } = [];
    public bool HasDownloads => Downloads.Count > 0;
    public bool HasActiveDownloads => Downloads.Any(item => item.IsActive);
    public int ActiveDownloadCount => Downloads.Count(item => item.IsActive);

    public TorrentDownloadsViewModel(TorrentDownloadService torrent) => _torrent = torrent;

    public async Task DownloadAsync(
        string name,
        string source,
        string magnetUri,
        string destinationFolder,
        IProgress<TorrentDownloadProgress>? observer,
        CancellationToken cancellationToken)
    {
        var item = new TorrentDownloadItemViewModel(name, source, destinationFolder);
        Downloads.Insert(0, item);
        NotifyCounts();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            item.CancellationToken);
        var progress = new Progress<TorrentDownloadProgress>(value =>
        {
            item.Report(value);
            observer?.Report(value);
        });

        try
        {
            await _torrent.DownloadMagnetAsync(magnetUri, destinationFolder, progress, linked.Token);
            item.MarkCompleted();
        }
        catch (OperationCanceledException)
        {
            item.MarkCancelled();
            throw;
        }
        catch
        {
            item.MarkFailed();
            throw;
        }
        finally
        {
            NotifyCounts();
        }
    }

    public void CancelAll()
    {
        foreach (TorrentDownloadItemViewModel item in Downloads.Where(item => item.IsActive).ToList())
            item.CancelCommand.Execute(null);
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(HasActiveDownloads));
        OnPropertyChanged(nameof(ActiveDownloadCount));
    }

    public void Dispose()
    {
        CancelAll();
        foreach (TorrentDownloadItemViewModel item in Downloads)
            item.Dispose();
    }
}
