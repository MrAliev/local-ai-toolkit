using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Repository.Tests;

/// <summary>
/// One shape for the writes that replace a document in place, in place of the five this
/// repository had grown (#399).
///
/// The axis is recoverability rather than importance, settled on #392: the generation pointer is
/// as important as anything in the runtime tree and still needs no flush, because every field of
/// it is derived and the next run computes the same value from the same repository. Losing it
/// costs recomputation, not a fact that existed nowhere else. So <c>AtomicWrite.Recomputable</c>
/// and <c>AtomicWrite.Irrecoverable</c> are the two states, and each call site says which it is.
///
/// What is not tested here, and cannot honestly be: that <c>Irrecoverable</c> survives a power
/// cut. The flush is asserted by nothing — it is a promise to the operating system, and a test
/// of it would prove the mock. Said here rather than covered by a green test that means nothing.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-atomic-file-" + Guid.NewGuid().ToString("N"));

    private sealed record Document(int Version, string Text);

    [Fact]
    public void A_document_is_written_and_the_temporary_does_not_survive()
    {
        var path = Path.Combine(_root, "state.json");

        AtomicFile.WriteJson(path, new Document(1, "first"), LocalAiJson.Strict, AtomicWrite.Recomputable);

        Assert.Equal(
            new Document(1, "first"),
            JsonSerializer.Deserialize<Document>(File.ReadAllText(path), LocalAiJson.Strict));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// The directory is the helper's business: every call site created it first, and the one that
    /// forgot would fail on a machine where the runtime tree had not been laid out yet.
    /// </summary>
    [Fact]
    public void The_destination_directory_is_created()
    {
        var path = Path.Combine(_root, "nested", "deeper", "state.json");

        AtomicFile.WriteJson(path, new Document(2, "nested"), LocalAiJson.Strict, AtomicWrite.Irrecoverable);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void An_existing_document_is_replaced_whole()
    {
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteJson(path, new Document(1, "a longer first document"), LocalAiJson.Strict, AtomicWrite.Recomputable);

        AtomicFile.WriteJson(path, new Document(2, "second"), LocalAiJson.Strict, AtomicWrite.Recomputable);

        Assert.Equal(
            new Document(2, "second"),
            JsonSerializer.Deserialize<Document>(File.ReadAllText(path), LocalAiJson.Strict));
    }

    /// <summary>
    /// The failure this exists for, and the reason the filter names two exception types: Windows
    /// raises <c>UnauthorizedAccessException</c> — not <c>IOException</c>, and it does not derive
    /// from one — when a reader's handle denies the replace. Verified on `current.json` in #427,
    /// where a filter naming only <c>IOException</c> let exactly this through.
    /// </summary>
    [Fact]
    public async Task A_replace_a_reader_is_denying_is_retried_until_the_reader_lets_go()
    {
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteJson(path, new Document(1, "first"), LocalAiJson.Strict, AtomicWrite.Recomputable);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(75), TestContext.Current.CancellationToken);
                held.Dispose();
            },
            TestContext.Current.CancellationToken);

        try
        {
            // A budget far larger than the release, so what passes is the retry rather than a
            // machine idle enough to win the race first time.
            AtomicFile.WriteJson(
                path,
                new Document(2, "second"),
                LocalAiJson.Strict,
                AtomicWrite.Recomputable.Retrying(TimeSpan.FromSeconds(30)));
            await release;
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(
            new Document(2, "second"),
            JsonSerializer.Deserialize<Document>(File.ReadAllText(path), LocalAiJson.Strict));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// And without a budget it does not wait: a write with no reader to race is the common case,
    /// and paying a retry loop for it would be paying for a race that cannot happen.
    /// </summary>
    [Fact]
    public void Without_a_budget_a_denied_replace_is_not_retried()
    {
        var path = Path.Combine(_root, "state.json");
        AtomicFile.WriteJson(path, new Document(1, "first"), LocalAiJson.Strict, AtomicWrite.Recomputable);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<SystemException>(() => AtomicFile.WriteJson(
            path,
            new Document(2, "second"),
            LocalAiJson.Strict,
            AtomicWrite.Recomputable));

        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// A write that throws leaves nothing behind either. The temporary carries a fresh GUID, so a
    /// leaked one is not overwritten by the next attempt — it accumulates in the runtime tree
    /// until somebody notices.
    /// </summary>
    [Fact]
    public void A_failed_write_leaves_no_temporary()
    {
        var path = Path.Combine(_root, "state.json");

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(
            path,
            _ => throw new InvalidOperationException("the content could not be produced"),
            AtomicWrite.Irrecoverable));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// The stream overload is what the binary formats use — the index, the semantic index, the
    /// checkpoint — so it has to be the same write, not a second one beside it.
    /// </summary>
    [Fact]
    public void Bytes_go_through_the_same_write_as_documents()
    {
        var path = Path.Combine(_root, "payload.bin");

        AtomicFile.Write(path, stream => stream.Write([1, 2, 3, 4]), AtomicWrite.Irrecoverable);

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
