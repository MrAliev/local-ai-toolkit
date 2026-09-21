using System.Diagnostics;
using System.Reflection;

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
    [InlineData("repo status --help --bogus")]
    [InlineData("repo status --bogus")]
    public async Task Unknown_arguments_are_refused(string arguments)
    {
        var result = await Run("en", arguments);
        Assert.NotEqual(0, result.Exit);
    }

    private static async Task<(int Exit, string Output, string Error)> Run(string language, string arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
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
