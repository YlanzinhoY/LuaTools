using LuaToolsGui.Models;
using LuaToolsGui.Resources;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CanIRunItModelsTests
{
    [Fact]
    public void DegradedResultUsesSafeLocalizedFallbackText()
    {
        var result = new CanIRunItAnalysis
        {
            Verdict = "inconclusive",
            Degraded = true,
            Summary = "",
        };
        var component = new CanIRunItComponent
        {
            Component = "GPU",
            Status = "unknown",
            Explanation = "",
        };

        Assert.Equal(Strings.CanIRunIt_Verdict_Inconclusive, result.VerdictLabel);
        Assert.Equal(Strings.CanIRunIt_FallbackSummary, result.SummaryDisplay);
        Assert.Equal(Strings.CanIRunIt_FallbackComponent, component.ExplanationDisplay);
        Assert.Equal(Strings.CanIRunIt_Status_Unknown, component.StatusLabel);
    }
}
