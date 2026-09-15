using CodeSearch.Mcp;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class UsageOutcomeTests
{
    [Theory]
    [InlineData("/absolute.cs", 1)]
    [InlineData("relative.cs", 0)]
    public async Task Invalid_navigation_is_refused_before_any_model_is_used(string path, int line)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "go_to_definition");
        await CodeSearchTools.GoToDefinition(null!, null!, path, line, cancellationToken: TestContext.Current.CancellationToken);
        var report = call.Complete();
        Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
    }
}
