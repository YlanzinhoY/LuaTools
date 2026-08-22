using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class R2AchievementServiceTests
{
    [Fact]
    public void Parse_SortsIdsAndUsesSavedR2State()
    {
        const string schema = """
        {
          "10": { "displayName": "Templar", "description": "Complete a hunt", "earned": 0 },
          "2":  { "displayName": "Second",  "description": "Second description", "earned": 1 }
        }
        """;
        const string state = """
        {
          "10": { "earned": "1", "earned_time": "1234" },
          "2":  { "earned": false }
        }
        """;

        var result = R2AchievementService.Parse(3751950, 66088, @"C:\state.json", true, schema, state);

        Assert.Equal([2, 10], result.Achievements.Select(a => a.Id));
        Assert.False(result.Achievements[0].Earned); // saved state overrides the schema default
        Assert.True(result.Achievements[1].Earned);
        Assert.Equal(1234, result.Achievements[1].EarnedTime);
        Assert.Equal("ACObsidian_Ach_10", result.Achievements[1].ApiName);
        Assert.Equal(1, result.EarnedCount);
    }

    [Fact]
    public void Parse_MissingStateFallsBackToSchemaDefaults()
    {
        const string schema = """
        {
          "1": { "displayName": "One", "description": "First", "earned": true },
          "bad-key": { "displayName": "Ignored", "earned": true }
        }
        """;

        var result = R2AchievementService.Parse(3751950, 66088, "state.json", false, schema, null);

        var achievement = Assert.Single(result.Achievements);
        Assert.True(achievement.Earned);
        Assert.False(result.StateFileExists);
    }

    [Fact]
    public void Supports_IsLimitedToTheKnownBlackFlagBinding()
    {
        Assert.True(R2AchievementService.Supports(R2AchievementService.BlackFlagSteamAppId));
        Assert.False(R2AchievementService.Supports(123));
    }
}
