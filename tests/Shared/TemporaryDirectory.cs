namespace LocalAi.Tests.Shared;

/// <summary>
/// A directory under the system temporary path that is actually gone afterwards.
///
/// The fixtures here build real Git repositories, because every mistake they exist to catch is
/// about what is on disk. Git marks everything it writes under <c>.git/objects</c> read-only,
/// and <see cref="Directory.Delete(string, bool)"/> refuses to remove a read-only file — so the
/// delete threw, each fixture swallowed the exception on purpose (a temporary directory left
/// behind must not be reported as a failure of the thing under test), and the tree stayed.
///
/// Every run left another one. Counted before this was written: 22 700 directories in the
/// user's temporary path, of which one fixture's repositories were 1 109 and another's 297.
///
/// Clearing the attribute before the delete is the whole of the fix. What still cannot be
/// removed — a file another process holds open — is still swallowed, for the original reason.
///
/// Not for a tree that contains a reparse point. The walk below is <see
/// cref="SearchOption.AllDirectories"/>, which follows a junction rather than stopping at it, so
/// a fixture that builds one — PublishScriptGuardTests does, on purpose — would have its
/// attributes cleared through the link. That fixture keeps its own delete, which never walks.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    /// <param name="name">
    /// Names the fixture in a directory listing, so one that does leak can be traced back to
    /// what made it. A GUID is appended: tests in one assembly run in parallel.
    /// </param>
    internal TemporaryDirectory(string name)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public void Dispose() => Delete(Path);

    /// <summary>
    /// Also called directly, by the fixtures that own their path rather than this object.
    /// Deleting an absent directory is not an error: a test may have moved or removed it.
    /// </summary>
    internal static void Delete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            ClearReadOnly(directory);
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The attribute is carried by files rather than by the directories holding them, and
    /// <see cref="Directory.Delete(string, bool)"/> stops at the first one it cannot remove —
    /// so this clears the whole tree before the delete starts rather than reacting to a throw.
    /// </summary>
    private static void ClearReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(
                     directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
