using System.Text;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CanIRunItServiceTests
{
    [Fact]
    public void ProcessBoundaryUsesUtf8ForAllRedirectedText()
    {
        var start = CanIRunItService.CreateStartInfo("can-i-run-it.exe");

        Assert.Equal(Encoding.UTF8.CodePage, start.StandardOutputEncoding?.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, start.StandardErrorEncoding?.CodePage);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }
}
