using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;
using ModelContextProtocol.Server;

namespace LocalLm.Tests;

/// <summary>
/// What the tool list tells an agent, checked against the code that decides it. Each row pins
/// the claim that was false, not the sentence around it.
/// </summary>
public sealed class ToolTextsSayWhatTheCodeDoesTests
{
    /// <summary>A relaxed residency policy lets a partial or CPU load through, with a warning.</summary>
    [Fact]
    public void Preflight_does_not_promise_full_vram_under_every_policy()
    {
        var description = Flat(Tool("local_model_preflight"));

        Assert.DoesNotContain("returns full-VRAM residency proof", description, StringComparison.Ordinal);
        Assert.Contains(nameof(ModelResidencyPolicy.RequireFullVram), description, StringComparison.Ordinal);
        Assert.Contains(nameof(ModelResidencyPolicy.AllowPartialOffload), description, StringComparison.Ordinal);
        Assert.Contains(nameof(ModelResidencyPolicy.AllowCpu), description, StringComparison.Ordinal);
        Assert.Contains("degradation warning", description, StringComparison.Ordinal);
    }

    /// <summary>Files share one character budget, so "full content" is not what is sent.</summary>
    [Fact]
    public void Ask_local_states_the_file_count_and_the_shared_budget()
    {
        var files = Flat(Parameter("ask_local", "files"));
        var maxFiles = (int)Constant(typeof(LocalTasks), "MaxAskFiles");
        var budget = (int)Constant(typeof(LocalTasks), "MaxPromptChars");

        Assert.DoesNotContain("full content", files, StringComparison.Ordinal);
        Assert.Contains($"at most {maxFiles}", files, StringComparison.Ordinal);
        Assert.Contains(
            $"one {budget.ToString("N0", CultureInfo.InvariantCulture)}-character budget",
            files,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Triage takes the first context the broker loads, from the largest down within its own
    /// policy's ceiling, and the residency policy decides what "loads" means.
    /// </summary>
    [Fact]
    public void Triage_does_not_promise_the_largest_context_that_fits_in_vram()
    {
        var description = Flat(Tool("triage_log"));

        Assert.DoesNotContain("largest context that actually fits in VRAM", description, StringComparison.Ordinal);
        Assert.Contains("up to the triage policy's ceiling", description, StringComparison.Ordinal);
        Assert.Contains("unless the residency policy was relaxed", description, StringComparison.Ordinal);
    }

    private static object Constant(Type type, string name) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static MethodInfo Method(string tool) => typeof(LocalLmTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);

    private static string Tool(string tool) =>
        Method(tool).GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string Parameter(string tool, string parameter) =>
        Method(tool).GetParameters().Single(candidate => candidate.Name == parameter)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;
}
