using System.Security.Cryptography;
using System.Text;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed partial class LocalTasksTests
{
    [Fact]
    public async Task Compiler_diagnostics_override_a_model_that_falsely_reports_a_clean_build()
    {
        var client = new FakeLocalModelClient { Answer = "No compiler errors; only warnings." };
        var tasks = new LocalTasks(client);

        var result = await tasks.TriageLogAsync(
            path: null,
            text: CompilerFailureLog(),
            question: "Which compiler errors occurred and how many?",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Contains("CS7036", result.Answer, StringComparison.Ordinal);
        Assert.Contains("CS0117", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("No compiler errors", result.Answer, StringComparison.Ordinal);
        AssertCompilerEvidence(result, CompilerFailureLog(), firstDiagnosticLine: 1);
    }

    [Fact]
    public async Task Compiler_diagnostics_survive_false_map_and_reduce_summaries()
    {
        var client = new FakeLocalModelClient
        {
            MaximumPreflightContextTokens = 2048,
            Answer = "No compiler errors; only warnings."
        };
        var source = "FIRST_BOUNDARY\n" + new string('x', 25_000) + "\n" +
                     CompilerFailureLog() + "\n" + new string('y', 25_000) + "\nLAST_BOUNDARY";
        var tasks = new LocalTasks(client);

        var result = await tasks.TriageLogAsync(
            path: null,
            text: source,
            question: "Which compiler errors occurred and how many?",
            model: null,
            TestContext.Current.CancellationToken);

        var mapCalls = client.Calls.Where(call =>
            call.Prompt.Contains("BEGIN LOG FRAGMENT", StringComparison.Ordinal)).ToArray();
        Assert.True(mapCalls.Length > 1);
        Assert.True(client.Calls.Count > mapCalls.Length);
        Assert.Contains("CS7036", result.Answer, StringComparison.Ordinal);
        Assert.Contains("CS0117", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("No compiler errors", result.Answer, StringComparison.Ordinal);
        AssertCompilerEvidence(result, source, firstDiagnosticLine: 3);
    }

    private static void AssertCompilerEvidence(LocalResult result, string source, long firstDiagnosticLine)
    {
        var evidence = Assert.IsType<CompilerLogReport>(result.CompilerDiagnostics);
        Assert.Equal("errors_found", evidence.AnalysisStatus);
        Assert.Equal(7, evidence.DiagnosticLineCount);
        Assert.Equal(5, evidence.CodeCounts["CS7036"]);
        Assert.Equal(2, evidence.CodeCounts["CS0117"]);
        Assert.Equal(Enumerable.Range(0, 7).Select(index => firstDiagnosticLine + index),
            evidence.Diagnostics.Select(item => item.SourceLine));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            evidence.SourceTextSha256);
        Assert.Equal("failed", Assert.IsType<CompilerModelVerification>(result.DiagnosticVerification).Status);
    }

    private static string CompilerFailureLog() => string.Join('\n',
        Enumerable.Range(1, 5).Select(line =>
            $@"R:\repo\Service.cs({line},8): error CS7036: Missing required argument. [R:\repo\App.csproj]")
        .Concat(Enumerable.Range(6, 2).Select(line =>
            $@"R:\repo\Service.cs({line},8): error CS0117: Type has no such member. [R:\repo\App.csproj]")));
}
