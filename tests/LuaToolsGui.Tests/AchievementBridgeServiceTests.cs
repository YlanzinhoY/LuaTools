using LuaToolsGui.Services;
using System.IO;
using Xunit;

namespace LuaToolsGui.Tests;

public class AchievementBridgeServiceTests
{
    [Fact]
    public void FindExecutable_PrefersDevelopmentOverride()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"achievement-bridge-{Guid.NewGuid():N}.exe");
        string? previous = Environment.GetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH");
        try
        {
            File.WriteAllBytes(tempFile, []);
            Environment.SetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH", tempFile);
            Assert.Equal(tempFile, AchievementBridgeService.FindExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH", previous);
            File.Delete(tempFile);
        }
    }
}
