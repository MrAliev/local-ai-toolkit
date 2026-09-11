using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The page a person confirms before anything is written named no file at all — six of them in
/// a home directory, agreed to by a choice title. The gate's line beside it has always named
/// its `settings.json`.
/// </summary>
public sealed class TheReviewPageNamesTheFilesTests
{
    private const string ClaudeJson = @"C:\Users\x\.claude.json";
    private const string ClaudeMd = @"C:\Users\x\.claude\CLAUDE.md";
    private const string ClaudeSkill = @"C:\Users\x\.claude\skills\localai\SKILL.md";

    private static AgentSnapshot Detected(AgentKind kind) =>
        new(
            kind,
            new FileMetadataSnapshot($@"C:\{kind}\client.exe", true, 1, null, null),
            FileMetadataSnapshot.Absent(string.Empty),
            FileMetadataSnapshot.Absent(string.Empty));

    private static AgentIntegrationPageViewModel Page(
        Func<string, AgentChoice, IReadOnlyList<string>> preview)
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyDetection([Detected(AgentKind.Claude), Detected(AgentKind.Codex)]);
        page.UseConfigurationPreview(preview);
        return page;
    }

    [Fact]
    public void Every_file_the_choice_will_write_is_named()
    {
        var page = Page((agent, _) => agent == "claude"
            ? [ClaudeJson, ClaudeMd, ClaudeSkill]
            : []);

        var review = page.ReviewText;

        Assert.Contains(ClaudeJson, review, StringComparison.Ordinal);
        Assert.Contains(ClaudeMd, review, StringComparison.Ordinal);
        Assert.Contains(ClaudeSkill, review, StringComparison.Ordinal);
    }

    /// <summary>
    /// A preview holds only files that would change, so a re-run over a current machine returns
    /// none. Saying nothing there would read as "this client is left alone", which is the one
    /// thing it must not be mistaken for when the choice is full integration.
    /// </summary>
    [Fact]
    public void A_client_already_current_says_so_rather_than_looking_untouched()
    {
        var page = Page((_, _) => []);

        var review = page.ReviewText;

        Assert.Contains(AgentChoice.McpAndInstructions.Title(), review, StringComparison.Ordinal);
        Assert.Contains("already", review, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nothing is written, so nothing is named — the title is the whole of it.</summary>
    [Fact]
    public void A_client_left_unchanged_names_no_file()
    {
        var page = Page((_, choice) => choice == AgentChoice.NoChange
            ? []
            : [ClaudeJson]);
        page.SetChoice("claude", AgentChoice.NoChange);
        page.SetChoice("codex", AgentChoice.NoChange);

        var review = page.ReviewText;

        Assert.DoesNotContain(ClaudeJson, review, StringComparison.Ordinal);
        Assert.DoesNotContain("already", review, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The combo is live, so the text follows the choice rather than the first read.</summary>
    [Fact]
    public void Changing_the_choice_changes_what_is_named()
    {
        var page = Page((agent, choice) => agent != "claude"
            ? []
            : choice == AgentChoice.McpOnly
                ? [ClaudeJson]
                : [ClaudeJson, ClaudeMd, ClaudeSkill]);

        Assert.Contains(ClaudeSkill, page.ReviewText, StringComparison.Ordinal);

        page.SetChoice("claude", AgentChoice.McpOnly);

        Assert.DoesNotContain(ClaudeSkill, page.ReviewText, StringComparison.Ordinal);
        Assert.Contains(ClaudeJson, page.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing seeded the previews — the wizard has not read the machine yet, or could not.
    /// Naming no file is right then; claiming there is nothing to write is not.
    /// </summary>
    [Fact]
    public void A_page_that_was_never_given_a_preview_claims_nothing()
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyDetection([Detected(AgentKind.Claude)]);

        Assert.DoesNotContain("already", page.ReviewText, StringComparison.OrdinalIgnoreCase);
    }
}
