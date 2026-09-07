using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// The fixtures in this suite build real Git repositories in the temporary path and delete them
/// afterwards. They did not: Git marks its object files read-only, the recursive delete refused
/// to remove them, and every fixture caught that exception and moved on. What was left behind is
/// measurable — 22 700 directories in the temporary path when this was written.
///
/// A read-only file is the case, so a read-only file is what the tests make. Nothing here runs
/// Git: the attribute is the whole of what Git contributes, and a test that shelled out to it
/// would be slower and would prove less.
/// </summary>
public sealed class ATemporaryDirectoryGoesAwayEvenWhenGitLockedItTests
{
    [Fact]
    public void A_read_only_file_does_not_keep_the_directory_alive()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "temporary-directory-tests-" + Guid.NewGuid().ToString("N"));
        var objects = Path.Combine(root, "objects");
        Directory.CreateDirectory(objects);
        var file = Path.Combine(objects, "8f2a1c");
        File.WriteAllText(file, "an object Git wrote");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        TemporaryDirectory.Delete(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void The_directory_is_created_on_construction_and_gone_after_disposal()
    {
        string path;
        using (var directory = new TemporaryDirectory("temporary-directory-tests"))
        {
            path = directory.Path;
            Assert.True(Directory.Exists(path));
            File.WriteAllText(directory.Combine("inside.txt"), "content");
        }

        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// A fixture may have moved or removed its own directory before disposal runs, and a throw
    /// there would fail a test that had already passed.
    /// </summary>
    [Fact]
    public void Deleting_a_directory_that_is_not_there_is_not_an_error()
    {
        TemporaryDirectory.Delete(
            Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N")));
    }
}
