using LuaToolsGui.Models;
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
}
