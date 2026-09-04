using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class TorrentDownloadServiceTests
{
    [Fact]
    public void NormalizeMagnetUri_ConvertsNumberedTrackerKeys()
    {
        const string magnet = "magnet:?xt=urn:btih:abc&tr.1=udp%3A%2F%2Fone&tr.2=https%3A%2F%2Ftwo";

        string normalized = TorrentDownloadService.NormalizeMagnetUri(magnet);

        Assert.Equal(
            "magnet:?xt=urn:btih:abc&tr=udp%3A%2F%2Fone&tr=https%3A%2F%2Ftwo",
            normalized);
    }

    [Fact]
    public void NormalizeMagnetUri_PreservesStandardTrackerKeys()
    {
        const string magnet = "magnet:?xt=urn:btih:abc&tr=udp%3A%2F%2Ftracker";

        Assert.Equal(magnet, TorrentDownloadService.NormalizeMagnetUri(magnet));
    }
}
