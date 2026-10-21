using LocalAi.Contracts;
using LocalAi.Broker.Client;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed partial class LocalTasksTests
{
    private const string CorrectCompilerInventory = "{\"diagnosticLineCount\":7,\"codeCounts\":{\"CS7036\":5,\"CS0117\":2},\"buildSummaryErrors\":[]}";

    [Fact]
    public async Task Malformed_compiler_inventory_gets_one_source_checked_retry()
    {
        var client = new FakeLocalModelClient { QueueDuration = TimeSpan.FromSeconds(2), ExecutionDuration = TimeSpan.FromSeconds(3) };
        client.Answers.Enqueue("Compiler failed; not JSON.");
        client.Answers.Enqueue(CorrectCompilerInventory);
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.DiagnosticVerification?.Status);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), result.Receipt.QueueDuration);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Receipt.ExecutionDuration);
        Assert.Equal(7, result.CompilerDiagnostics?.DiagnosticLineCount);
        Assert.Contains("source_text_sha256=", result.Answer, StringComparison.Ordinal);
        Assert.Contains("No prose", client.Calls[1].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_malformed_inventory_stops_after_one_retry_and_keeps_source_evidence()
    {
        var client = new FakeLocalModelClient { Answer = "Nothing failed." };
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("failed", result.DiagnosticVerification?.Status);
        Assert.Equal(7, result.CompilerDiagnostics?.DiagnosticLineCount);
        Assert.Contains("CS7036", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing failed", result.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_single_JSON_fence_can_be_normalized_without_another_model_job()
    {
        var client = new FakeLocalModelClient { Answer = "```json\n" + CorrectCompilerInventory + "\n```" };
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.DiagnosticVerification?.Status);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Format_retry_does_not_accept_changed_counts()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("not JSON");
        client.Answers.Enqueue(CorrectCompilerInventory.Replace("\"CS7036\":5", "\"CS7036\":4", StringComparison.Ordinal));
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("failed", result.DiagnosticVerification?.Status);
        Assert.Equal(5, result.CompilerDiagnostics?.CodeCounts["CS7036"]);
        Assert.Contains("code_count_mismatch:CS7036", result.DiagnosticVerification!.Reasons);
    }

    [Fact]
    public async Task Compiler_prompts_request_JSON_without_the_generic_prose_instruction()
    {
        var client = new FakeLocalModelClient { Answer = CorrectCompilerInventory };
        await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);
        var call = Assert.Single(client.Calls);
        Assert.DoesNotContain("say so briefly", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("No prose", call.System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Noncompiler_logs_do_not_get_a_format_retry()
    {
        var client = new FakeLocalModelClient { Answer = "Database timeout caused the runtime failure." };
        var result = await new LocalTasks(client).TriageLogAsync(null, "Database timeout after 30 seconds.", null, null, TestContext.Current.CancellationToken);
        Assert.Single(client.Calls);
        Assert.Null(result.CompilerDiagnostics);
        Assert.Equal(client.Answer, result.Answer);
    }

    [Fact]
    public async Task Cancellation_after_the_first_answer_does_not_start_a_format_retry()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (_, _) => { cancellation.Cancel(); return "not JSON"; }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, cancellation.Token));
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task A_failed_repair_job_does_not_discard_already_scanned_source_evidence()
    {
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (_, attempt) => attempt == 1 ? "not JSON"
                : throw new BrokerJobFailedException(Guid.NewGuid(), "repair_backend_failed")
        };
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("failed", result.DiagnosticVerification?.Status);
        Assert.Equal(7, result.CompilerDiagnostics?.DiagnosticLineCount);
        Assert.Contains("CS7036", result.Answer, StringComparison.Ordinal);
        Assert.Equal("repair_job_failed", result.DiagnosticVerification?.RepairFailureReason);
        Assert.False(result.DiagnosticVerification?.RepairTimingComplete);
        Assert.Equal(1, result.DiagnosticVerification?.RepairAttempts);
    }

    [Fact]
    public async Task Incorrect_inventory_inside_a_fence_is_not_accepted_or_retried_as_format_only()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "```json\n" + CorrectCompilerInventory.Replace("\"CS7036\":5", "\"CS7036\":4", StringComparison.Ordinal) + "\n```"
        };
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);
        Assert.Single(client.Calls);
        Assert.Equal("failed", result.DiagnosticVerification?.Status);
        Assert.True(result.DiagnosticVerification?.FormatNormalized);
        Assert.Equal(0, result.DiagnosticVerification?.RepairAttempts);
    }

    [Fact]
    public async Task Extra_prose_is_not_arbitrarily_stripped_to_find_a_successful_inventory()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("Explanation before " + CorrectCompilerInventory + " explanation after.");
        client.Answers.Enqueue(CorrectCompilerInventory);
        var result = await new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("passed", result.DiagnosticVerification?.Status);
        Assert.Equal(1, result.DiagnosticVerification?.RepairAttempts);
        Assert.False(result.DiagnosticVerification?.FormatNormalized);
        Assert.Contains("response_not_json", result.DiagnosticVerification!.InitialReasons);
    }

    [Fact]
    public async Task An_inventory_larger_than_the_accepted_reply_budget_does_not_start_a_repair()
    {
        var client = new FakeLocalModelClient { MaximumPreflightContextTokens = 2048, Answer = "not JSON" };
        var source = string.Join('\n', Enumerable.Range(1000, 200).Select(code =>
            $"R:\\repo\\Service.cs(1,1): error CS{code}: Compiler diagnostic. [R:\\repo\\App.csproj]"));
        var result = await new LocalTasks(client).TriageLogAsync(null, source, null, null, TestContext.Current.CancellationToken);
        Assert.Equal("errors_found", result.CompilerDiagnostics?.AnalysisStatus);
        Assert.Equal(200, result.CompilerDiagnostics?.DiagnosticLineCount);
        Assert.Equal(0, result.DiagnosticVerification?.RepairAttempts);
        Assert.Equal("repair_context_budget", result.DiagnosticVerification?.RepairFailureReason);
        Assert.DoesNotContain(client.Calls, call => call.Prompt.Contains("programmatically verified source counts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_during_a_failed_repair_is_not_replaced_by_deterministic_fallback()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (_, attempt) =>
            {
                if (attempt == 1) return "not JSON";
                cancellation.Cancel();
                throw new BrokerJobFailedException(Guid.NewGuid(), "cancelled");
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTasks(client).TriageLogAsync(null, CompilerFailureLog(), null, null, cancellation.Token));
        Assert.Equal(2, client.Calls.Count);
    }
}
