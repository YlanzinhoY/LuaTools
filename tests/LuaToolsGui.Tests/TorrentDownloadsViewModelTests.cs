using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class TorrentDownloadsViewModelTests
{
    [Fact]
    public void DownloadItem_TracksProgressAndCanBeCancelled()
    {
        using var item = new TorrentDownloadItemViewModel("Ubuntu", "Torrent", @"C:\Downloads");

        item.Report(new TorrentDownloadProgress(42.5, 1_048_576, 18, "Downloading"));

        Assert.True(item.IsActive);
        Assert.Equal(42.5, item.Progress);
        Assert.Contains("18", item.StatusText);

        item.CancelCommand.Execute(null);

        Assert.True(item.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void DownloadItem_CompletionDisablesCancellation()
    {
        using var item = new TorrentDownloadItemViewModel("Ubuntu", "Torrent", @"C:\Downloads");

        item.MarkCompleted();

        Assert.False(item.IsActive);
        Assert.Equal(100, item.Progress);
        Assert.False(item.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedDownload_IsRemovedFromTheList()
    {
        using var downloads = new TorrentDownloadsViewModel(new TorrentDownloadService());

        await Assert.ThrowsAsync<ArgumentException>(() => downloads.DownloadAsync(
            "Broken game",
            "Torrent",
            "not-a-magnet",
            @"C:\Downloads",
            observer: null,
            CancellationToken.None));

        Assert.Empty(downloads.Downloads);
        Assert.False(downloads.HasDownloads);
    }

    [Fact]
    public void ClearCompleted_RemovesOnlyCompletedDownloads()
    {
        using var downloads = new TorrentDownloadsViewModel(new TorrentDownloadService());
        var active = new TorrentDownloadItemViewModel("Active", "Torrent", @"C:\Downloads");
        var completed = new TorrentDownloadItemViewModel("Completed", "Torrent", @"C:\Downloads");
        var cancelled = new TorrentDownloadItemViewModel("Cancelled", "Torrent", @"C:\Downloads");
        completed.MarkCompleted();
        cancelled.MarkCancelled();
        downloads.Downloads.Add(active);
        downloads.Downloads.Add(completed);
        downloads.Downloads.Add(cancelled);

        downloads.ClearCompletedCommand.Execute(null);

        Assert.Equal([active, cancelled], downloads.Downloads);
        Assert.DoesNotContain(completed, downloads.Downloads);
        Assert.False(downloads.HasCompletedDownloads);
    }
}
