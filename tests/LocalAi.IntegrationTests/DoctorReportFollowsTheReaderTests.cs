using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The doctor report answers in the reader's language and keeps its left columns English.
///
/// The report exists to be pasted into an issue, so the two halves are worth naming apart. The
/// detail is prose a person reads and it follows them. The marker, the check name and the enum
/// values are the skeleton: four characters, then a name padded to a fixed width, then the
/// detail — so a Russian paste and an English one still diff line for line, and the tokens an
/// agent was taught to look for are the same tokens.
///
/// A translation of `note` looks almost right and quietly breaks a fixed-width column. That is
/// the mistake this is here to catch.
/// </summary>
public sealed class DoctorReportFollowsTheReaderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-language-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void The_detail_is_Russian_for_a_Russian_reader()
    {
        using var reading = TestCulture.Reading("ru");

        var rendered = DoctorCommand.Render(DoctorCommand.Inspect(root));

        Assert.Contains("Здесь ничего не установлено.", rendered, StringComparison.Ordinal);
        Assert.Contains("Проблем", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_report_in_English_is_what_every_other_reader_gets()
    {
        var rendered = DoctorCommand.Render(DoctorCommand.Inspect(root));

        Assert.Contains("Nothing is installed here.", rendered, StringComparison.Ordinal);
        Assert.Contains("problem(s)", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The skeleton. Every line still starts with one of four four-character markers, and every
    /// check still answers to the name it always had.
    /// </summary>
    [Fact]
    public void The_markers_and_the_check_names_do_not_move()
    {
        using var reading = TestCulture.Reading("ru");
        new LocalAi.Contracts.UpdateCheckPolicyStore(root).Write(
            LocalAi.Contracts.UpdateCheckPolicy.Default with { Enabled = true });

        var rendered = DoctorCommand.Render(DoctorCommand.Inspect(root));

        Assert.Contains("unkn  update", rendered, StringComparison.Ordinal);
        foreach (var line in rendered
                     .Split('\n')
                     .Select(line => line.TrimEnd('\r'))
                     .Where(line => line.Length > 0))
        {
            Assert.True(
                line.StartsWith("ok  ", StringComparison.Ordinal) ||
                line.StartsWith("note", StringComparison.Ordinal) ||
                line.StartsWith("unkn", StringComparison.Ordinal) ||
                line.StartsWith("FAIL", StringComparison.Ordinal) ||
                line.StartsWith("Проблем", StringComparison.Ordinal),
                $"A report line begins with something that is not a marker: {line}");
        }

        // The eight a runtime root alone produces. The repository check needs a repository to
        // ask about, and pinning one here would make this test an assertion about the machine
        // it runs on rather than about the language it answers in.
        foreach (var name in new[]
                 {
                     "version", "launcher", "broker", "queue", "policy.models",
                     "policy.retention", "policy.languageServers", "update",
                 })
        {
            Assert.Contains(name, rendered, StringComparison.Ordinal);
        }

        // And whatever else the report happened to carry, so a name that is added later is
        // covered without this list having to be remembered.
        foreach (var check in DoctorCommand.Inspect(root).Checks)
        {
            Assert.Contains(check.Name, rendered, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A policy value and a command a reader is meant to type are the same in every language.
    /// </summary>
    [Fact]
    public void Values_and_commands_stay_as_they_are_typed()
    {
        using var reading = TestCulture.Reading("ru");

        var rendered = DoctorCommand.Render(DoctorCommand.Inspect(root));

        Assert.Contains("RequireFullVram", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "localai policy set --update-check on",
            rendered,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "", "No problems.")]
    [InlineData("en", "U", "No problems. 1 unanswered.")]
    [InlineData("en", "WU", "No problems. 1 worth reading, 1 unanswered.")]
    [InlineData("en", "FWU", "1 problem(s), 1 worth reading, 1 unanswered.")]
    [InlineData("en", "F", "1 problem(s), 0 worth reading.")]
    [InlineData("ru", "U", "Проблем нет. Без ответа 1.")]
    [InlineData("ru", "WU", "Проблем нет. Стоит прочитать 1, без ответа 1.")]
    [InlineData("ru", "FWU", "Проблем 1, стоит прочитать 1, без ответа 1.")]
    public void The_summary_counts_unanswered_checks_only_when_there_are_some(string language, string statuses, string summary)
    {
        using var reading = TestCulture.Reading(language);
        var checks = new List<DoctorCheck> { new("ok", DoctorStatus.Ok, "fine") };
        foreach (var status in statuses)
        {
            checks.Add(status switch
            {
                'F' => new DoctorCheck("failed", DoctorStatus.Failed, "broken"),
                'W' => new DoctorCheck("warned", DoctorStatus.Warning, "read me"),
                _ => new DoctorCheck("unanswered", DoctorStatus.Unknown, "no answer"),
            });
        }

        var rendered = DoctorCommand.Render(new DoctorReport(checks)).TrimEnd();

        Assert.EndsWith(Environment.NewLine + summary, rendered, StringComparison.Ordinal);
        if (statuses.Contains('U'))
            Assert.Contains("unkn  unanswered", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_language_carries_every_string()
    {
        var gaps = CliText.Catalogue.Gaps();

        Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
    }
}
