using System.Diagnostics;
using System.Reflection;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class CliHelpTests
{
    [Theory]
    [InlineData("en", "--help", "Usage:")]
    [InlineData("ru", "--help", "Использование:")]
    [InlineData("en", "repo --help", "localai repo status")]
    [InlineData("ru", "repo status --help", "localai repo status")]
    [InlineData("en", "help model pull", "--catalog-version")]
    [InlineData("en", "-h", "localai sync")]
    [InlineData("en", "sync --help", "--all-worktrees")]
    [InlineData("ru", "sync --help", "--all-worktrees")]
    [InlineData("en", "sync cancel --help", "localai sync cancel --root dir --operation operation-id")]
    [InlineData("ru", "sync cancel --help", "localai sync cancel --root dir --operation operation-id")]
    [InlineData("en", "docs prune --help", "--dry-run | --apply")]
    [InlineData("ru", "docs pin --help", "--package")]
    [InlineData("en", "docs unpin --help", "--package")]
    [InlineData("en", "documents index --help", "--source")]
    [InlineData("ru", "documents search --help", "--query")]
    public async Task Help_is_successful_stdout_without_executing_the_command(string language, string arguments, string expected)
    {
        var result = await Run(language, arguments);
        Assert.Equal(0, result.Exit);
        Assert.Contains(expected, result.Output);
        Assert.DoesNotContain("Usage:", result.Error);
    }

    [Theory]
    [InlineData("--help --bogus")]
    [InlineData("help missing")]
    [InlineData("repo status --bogus")]
    public async Task Unknown_arguments_are_refused(string arguments)
    {
        var result = await Run("en", arguments);
        Assert.NotEqual(0, result.Exit);
    }

    // Where Git finds no repository: each of these ran before the fix, and a sync or a hook
    // started here fails at once instead of indexing anything.
    [Theory]
    [InlineData("sync --base-only --help", "localai sync [--root dir] [--base-only]")]
    [InlineData("bootstrap --dry-run --help", "localai bootstrap --dry-run")]
    [InlineData("hook post-commit --root . --help", "localai hook <post-commit|")]
    [InlineData("repo status --help --bogus", "localai repo status")]
    public async Task A_help_flag_after_an_option_prints_help_and_runs_nothing(string arguments, string expected)
    {
        using var outside = new TemporaryDirectory("cli-help-outside-git");

        var result = await Run("en", arguments, outside.Path);

        Assert.Equal(0, result.Exit);
        Assert.Contains(expected, result.Output);
    }

    [Theory]
    [InlineData("en", "Usage:")]
    [InlineData("ru", "Использование:")]
    public async Task Sync_cancel_help_with_an_operation_id_never_requests_cancellation(string language, string expected)
    {
        using var outside = new TemporaryDirectory("cli-sync-cancel-help-outside-git");
        var result = await Run(language,
            "sync cancel --root . --operation 11111111-1111-1111-1111-111111111111 --help", outside.Path);

        Assert.Equal(0, result.Exit);
        Assert.Contains(expected, result.Output);
        Assert.Contains("localai sync cancel --root dir --operation operation-id", result.Output);
        Assert.DoesNotContain("SYNC_CANCEL_", result.Output);
    }

    [Theory]
    [InlineData("en", "sync cancel", "Usage: localai sync cancel")]
    [InlineData("ru", "sync cancel", "Использование: localai sync cancel")]
    [InlineData("en", "sync --operation invalid", "--operation requires a nonempty GUID.")]
    [InlineData("ru", "sync --operation invalid", "для --operation требуется непустой идентификатор GUID.")]
    public async Task Invalid_sync_operation_arguments_have_localized_diagnostics(string language, string arguments, string expected)
    {
        using var outside = new TemporaryDirectory("cli-sync-cancel-invalid-outside-git");
        var result = await Run(language, arguments, outside.Path);

        Assert.Equal(64, result.Exit);
        Assert.Contains(expected, result.Error);
        Assert.DoesNotContain("SYNC_CANCEL_REQUESTED", result.Output);
    }

    private static async Task<(int Exit, string Output, string Error)> Run(
        string language, string arguments, string? outsideGit = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        if (outsideGit is not null)
        {
            start.WorkingDirectory = outsideGit;
            start.Environment["GIT_DIR"] = Path.Combine(outsideGit, "no-repository");
        }
        start.Environment["LOCALAI_LANGUAGE"] = language;
        start.ArgumentList.Add(Assembly.Load("localai").Location);
        foreach (var argument in arguments.Split(' ')) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }
}
