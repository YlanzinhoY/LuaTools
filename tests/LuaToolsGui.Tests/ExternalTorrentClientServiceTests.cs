using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class ExternalTorrentClientServiceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\qBittorrent\\qbittorrent.exe\" \"%1\"", "C:\\Program Files\\qBittorrent\\qbittorrent.exe")]
    [InlineData("C:\\Apps\\client.exe %1", "C:\\Apps\\client.exe")]
    public void ExtractExecutableHandlesWindowsProtocolCommands(string command, string expected) =>
        Assert.Equal(expected, ExternalTorrentClientService.ExtractExecutable(command));

    [Fact]
    public void ExtractExecutableRejectsAnInvalidCommand() =>
        Assert.Null(ExternalTorrentClientService.ExtractExecutable("not-an-executable %1"));
}
