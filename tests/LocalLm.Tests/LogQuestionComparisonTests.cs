using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Mcp;
using LocalLm.Core;
using ModelContextProtocol.Protocol;

namespace LocalLm.Tests;

public sealed partial class LocalTasksTests
{
    [Fact]
    public async Task Default_log_triage_retains_an_explicit_whole_input_comparison()
    {
        const string source = "Restore completed.\nPassed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8";
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "triage_log");
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        var result = await new LocalTasks(new FakeLocalModelClient { Answer = "All tests passed." })
            .TriageLogAsync(path: null, text: source, question: null, model: null,
                TestContext.Current.CancellationToken);
        var report = CloudTokenMethodology.Apply(call.Complete(), call.Comparison.Scenario("request"),
            result.Answer, call.Comparison.Profile);
        Assert.Equal(O200kCloudTokenProfile.Instance.CountText(source),
            report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
    }

    [Theory]
    [InlineData(0, CloudContextPresence.NotPresent)]
    [InlineData(2000, CloudContextPresence.NotPresent)]
    [InlineData(2000, CloudContextPresence.AlreadyPresent)]
    [InlineData(2000, CloudContextPresence.Unknown)]
    public async Task A_log_question_does_not_credit_irrelevant_padding_as_cloud_context(
        int paddingLines, CloudContextPresence presence)
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-log-question-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string question = "Only the exact final xUnit summary row and skipped test name/reason. No setup summary.";
            const string summary = "Passed! - Failed: 0, Passed: 7, Skipped: 1, Total: 8\n[SKIP] OptionalFixture: feature unavailable";
            var path = Path.Combine(root, "test.log");
            await File.WriteAllTextAsync(path,
                string.Concat(Enumerable.Repeat("Restoring an unrelated setup package.\n", paddingLines)) + summary,
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient { Answer = summary, MaximumPreflightContextTokens = 2048 };
            var tasks = new LocalTasks(client);
            var response = await new UsageReporting(root).InvokeAsync("locallm", "triage_log", Guid.NewGuid(),
                async token => new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = await LocalLm.Mcp.LocalLmTools.TriageLog(tasks, path: path, question: question,
                            cancellationToken: token, sourceContext: presence,
                            contextBasis: CloudContextBasis.ExplicitTaskInput),
                    }],
                }, TestContext.Current.CancellationToken,
                requestJson: JsonSerializer.Serialize(new { path, question, sourceContext = presence,
                    contextBasis = CloudContextBasis.ExplicitTaskInput }));

            var report = UsageReportJson.Read(response.Meta!["localai/usage"]!.ToJsonString());
            Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Status);
            Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
            Assert.Contains(":" + presence + ":", report.Comparison.ScenarioId!);
            Assert.NotEmpty(client.Calls);
            Assert.All(client.Calls, call => Assert.Contains(question, call.Prompt, StringComparison.Ordinal));
            if (paddingLines > 0)
            {
                Assert.True(client.Calls.Count(call => call.Prompt.Contains("BEGIN LOG FRAGMENT", StringComparison.Ordinal)) > 1);
                Assert.Contains(client.Calls, call => !call.Prompt.Contains("BEGIN LOG FRAGMENT", StringComparison.Ordinal));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
