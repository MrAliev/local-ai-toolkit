using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.IntegrationTests;

public sealed class LogTriageRefusalCliTests
{
    /// <summary>
    /// No usable model is a service that is not there, like a model that is not installed:
    /// exit 69 with its own code, not an unexpected failure named after an exception type.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_triage_with_no_usable_model_is_a_refusal_rather_than_an_unexpected_failure(bool machineReadable)
    {
        const string refusal = "No local model could be used for log triage. Not installed: qwen2.5-coder:14b.";
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await LocalModelRun.ExecuteAsync("triage", "test", LocalTaskProfile.LogTriage,
            (_, _) => throw new NoTriageModelException(refusal), machineReadable,
            TestContext.Current.CancellationToken, new CommandStreams(output, error));

        Assert.Equal(69, exit);
        if (machineReadable)
        {
            using var envelope = JsonDocument.Parse(output.ToString());
            var failure = envelope.RootElement.GetProperty("error");
            Assert.Equal("model_not_available", failure.GetProperty("code").GetString());
            Assert.Equal(refusal, failure.GetProperty("message").GetString());
        }
        else
        {
            Assert.Equal(refusal, error.ToString().Trim());
        }
    }
}
