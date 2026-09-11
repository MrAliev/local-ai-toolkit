using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

/// <summary>
/// `index_refresh` hung for thirty-one minutes on a machine where the same `localai sync` ran
/// from a console in eight. The child it was waiting on was `git archive`, which had burned
/// 0.00 seconds of processor time and written no output at all — stopped before it began, not
/// slow.
///
/// The difference between the two runs was the parent. Under the MCP server, standard input is
/// the JSON-RPC pipe from the client: it never closes and never delivers, and a child that
/// reads it waits for ever. The sibling that runs git for the same purpose,
/// <c>RepoLocator.GitOutputBytes</c>, redirects standard input and closes it; this one did not.
///
/// Asserted on the start info rather than by reproducing the hang, which is the shape
/// <c>BrokerProcessTests.Broker_start_info_does_not_inherit_caller_stdio</c> already set: a test
/// that waits on a wedge either waits for ever or proves nothing.
/// </summary>
public sealed class ASnapshotDoesNotInheritTheCallersStdinTests
{
    [Fact]
    public void The_archive_start_info_redirects_every_stream()
    {
        var start = CommitSnapshot.ArchiveStartInfo(
            @"R:\Repo",
            @"C:\Temp\snapshot.zip",
            "HEAD");

        Assert.True(
            start.RedirectStandardInput,
            "git inherits the caller's standard input otherwise, and under an MCP server that " +
            "is a pipe which never closes");
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
    }

    /// <summary>
    /// And the launch above it, which is what closes the hole for everything below: `localai
    /// sync` is spawned by the MCP server, and every process it starts in turn — git among them
    /// — inherits whatever standard input it was given. Handing it a closed stream once covers
    /// descendants no test can name.
    /// </summary>
    [Fact]
    public void The_sync_the_server_starts_is_given_its_own_stdin()
    {
        var start = CodeSearch.Mcp.CodeSearchTools.SyncStartInfo(
            @"C:\LocalAi\bin\localai.exe",
            @"R:\Repo",
            200);

        Assert.True(
            start.RedirectStandardInput,
            "everything this sync starts inherits the stream it was given");
        Assert.False(start.UseShellExecute);
        Assert.Equal(
            ["sync", "--root", @"R:\Repo", "--max-inline-files", "200"],
            start.ArgumentList);
    }

    [Fact]
    public void The_archive_start_info_names_the_revision_and_the_output()
    {
        var start = CommitSnapshot.ArchiveStartInfo(
            @"R:\Repo",
            @"C:\Temp\snapshot.zip",
            "eaceb6f");

        Assert.Equal("git", start.FileName);
        Assert.Equal(
            ["archive", "--format=zip", @"--output=C:\Temp\snapshot.zip", "eaceb6f"],
            start.ArgumentList);
    }
}
