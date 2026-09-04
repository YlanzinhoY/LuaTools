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

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("magnet:?xt=urn:btih:ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", true)]
    [InlineData("https://example.com/file.torrent", false)]
    [InlineData("magnet:?dn=missing-info-hash", false)]
    [InlineData("magnet:?xt=urn:btih:too-short", false)]
    public void IsValidMagnetUri_RequiresBtihInfoHash(string value, bool expected)
    {
        Assert.Equal(expected, TorrentDownloadService.IsValidMagnetUri(value));
    }
}
