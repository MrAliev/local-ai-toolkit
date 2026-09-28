using System.Diagnostics;
using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// A sync never said how long it took. The only number on the screen was the one the overlay
/// builder printed for its own call — 6.7 s against 49.67 s measured — so a reader deciding
/// "have I time to rebuild the index before I search" was out by a factor of seven (#386).
///
/// The defect is a missing total, not a lying line: `Saved overlay …, in 6.6s.` is true of that
/// call, and the builder is right to time only what it does. So the sentence belongs to the
/// command that orchestrates the whole run.
///
/// This test takes the failing path, because a successful sync needs an embedding backend and
/// these tests never start one. The failure is the more useful of the two anyway: a run that
/// stopped after four hundred seconds is exactly the one whose cost the reader wants named.
/// </summary>
public sealed class ASyncSaysHowLongItTookTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-duration-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-duration-runtime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_sync_that_stopped_says_how_long_it_ran()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "Local AI Tests");
        Git("commit", "--allow-empty", "-m", "main");

        using var reading = TestCulture.Reading("en");
        // The sync's own writer, not the process's. Replacing Console.Error made this class race
        // every other one that did the same, and the collection that stopped it held only while
        // everybody remembered the attribute (#405).
        var written = new StringWriter();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CodeSearchSyncCommand.ExecuteAsync(
                _root,
                written,
                cancellationToken: TestContext.Current.CancellationToken,
                runtimeRoot: _runtimeRoot));

        Assert.Contains("Sync stopped after", written.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Deliberately not an assertion about particular words, in the shape
    /// <c>SyncOutputFollowsTheReaderTests</c> already uses: pinning the sentence would make this a
    /// copy of the resource file. That the two languages differ at all is the behaviour, and that
    /// the number does not move between them is the rule about invariant figures.
    /// </summary>
    [Fact]
    public void The_duration_lines_follow_the_reader_and_the_number_does_not()
    {
        var english = (CliText.SyncFinished("49.7"), CliText.SyncStoppedAfter("402.6"));

        using var reading = TestCulture.Reading("ru");

        var russian = (CliText.SyncFinished("49.7"), CliText.SyncStoppedAfter("402.6"));
        Assert.NotEqual(english.Item1, russian.Item1, StringComparer.Ordinal);
        Assert.NotEqual(english.Item2, russian.Item2, StringComparer.Ordinal);
        Assert.Contains("49.7", russian.Item1, StringComparison.Ordinal);
        Assert.Contains("402.6", russian.Item2, StringComparison.Ordinal);
    }

    private void Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        if (Directory.Exists(_root))
        {
            // Git leaves its object files read-only, and a recursive delete refuses them.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
