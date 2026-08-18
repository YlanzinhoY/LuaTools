using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class CloudRedirectServiceTests
{
    [Fact]
    public void CliFailureUsesStructuredJsonError()
    {
        string message = CloudRedirectService.ResolveCliFailureMessage(
            1,
            """{"success":false,"error":"Save file upload failed: slot.save"}""",
            "");

        Assert.Equal("Save file upload failed: slot.save", message);
    }

    [Fact]
    public void CliFailureFallsBackToStandardErrorWhenOutputIsNotJson()
    {
        string message = CloudRedirectService.ResolveCliFailureMessage(1, "unexpected output", "Provider failed");

        Assert.Equal("Provider failed", message);
    }

    [Fact]
    public void CliFailureFallsBackToExitCodeWithoutDetails()
    {
        string message = CloudRedirectService.ResolveCliFailureMessage(1, "", "");

        Assert.Equal(string.Format(Resources.Strings.CloudFix_CliExited, 1), message);
    }
}
