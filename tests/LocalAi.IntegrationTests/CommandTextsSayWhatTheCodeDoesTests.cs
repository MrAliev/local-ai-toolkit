using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// What the usage lines tell a reader, checked against what the dispatcher accepts. Each row
/// pins the claim that was false, not the sentence around it.
/// </summary>
public sealed class CommandTextsSayWhatTheCodeDoesTests
{
    /// <summary>
    /// Angle brackets mark a required operand elsewhere in the listing, so "brackets" alone
    /// reads as covering them too — and in Russian it does.
    /// </summary>
    [Fact]
    public void The_help_footer_names_the_brackets_that_mean_optional()
    {
        Assert.Contains("Optional arguments are in square brackets.", CliHelp.Render("localai"), StringComparison.Ordinal);

        using var reading = TestCulture.Reading("ru");
        Assert.Contains("в квадратных скобках", CliHelp.Render("localai"), StringComparison.Ordinal);
    }

    /// <summary>`localai policy` alone shows, and `localai semantic` alone reports status.</summary>
    [Fact]
    public void An_operation_the_dispatcher_defaults_is_not_shown_as_required()
    {
        var policy = CliHelp.Render("localai", "policy");
        var semantic = CliHelp.Render("localai", "semantic");

        Assert.DoesNotContain("<show|set>", policy, StringComparison.Ordinal);
        Assert.Contains("localai policy [show|set <option> <value>]", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("localai semantic <operation>", semantic, StringComparison.Ordinal);
        Assert.Contains("localai semantic [<operation> [options]]", semantic, StringComparison.Ordinal);
    }

    /// <summary>Each of the three config groups falls back to `show` when nothing follows it.</summary>
    [Fact]
    public void The_semantic_config_usage_marks_its_default_operation_optional()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-semantic-usage-" + Guid.NewGuid().ToString("N"));
        var error = new StringWriter();

        int exit;
        try
        {
            exit = SemanticNavigationCommand.Execute(["config", "unknown"], new CommandStreams(new StringWriter(), error), root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Assert.Equal(2, exit);
        Assert.Contains("localai semantic config [show|path|init]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("localai semantic lsp-config [show|path|init]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("localai semantic fallback-config [show|path|init]", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep a prune runs passes no live set, so a removed worktree's overlays under a kept
    /// generation stay until a sync that does pass one.
    /// </summary>
    [Fact]
    public void The_prune_usage_does_not_claim_the_sweep_a_sync_runs()
    {
        var english = Flat(CliText.RepoPruneUsage);
        Assert.DoesNotContain("exactly as localai sync does", english, StringComparison.Ordinal);
        Assert.Contains("deleted by the next localai sync", english, StringComparison.Ordinal);

        using var reading = TestCulture.Reading("ru");
        var russian = Flat(CliText.RepoPruneUsage);
        Assert.DoesNotContain("ровно так же, как это делает localai sync", russian, StringComparison.Ordinal);
        Assert.Contains("удалит следующий localai sync", russian, StringComparison.Ordinal);
    }

    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
