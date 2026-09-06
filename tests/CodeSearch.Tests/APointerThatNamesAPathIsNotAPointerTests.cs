using System.Text.Json;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The generation id in <c>current.json</c> becomes a path segment, and the value read back was
/// never checked to be one.
///
/// <c>RuntimeIndexLayout.ResolveBaseIndexPath</c> hands it to
/// <see cref="GenerationStore.IndexPath"/>, and <c>SemanticNavigationGateway.LoadCurrent</c> hands
/// it to <see cref="GenerationStore.ReadManifest"/>; both compose it with
/// <c>Path.Combine(generationsRoot, generationId, …)</c>, which does not resolve <c>..</c> and
/// drops the root entirely when the second part is absolute. So the file opened as this
/// repository's index was chosen by the contents of a document, with nothing asking whether that
/// document named a generation at all.
///
/// This is the asymmetry #402 is about, and the one protection of the launcher's pointer that has
/// a reason here: <c>CurrentPointerSnapshot</c> refuses its own value through
/// <c>LocalAiVersionName.IsSafe</c> before it becomes a directory name, at read time. A generation
/// id has an exact shape — <c>GenerationIdentity.Id</c> is <c>Convert.ToHexString</c> of a SHA-256,
/// lowercased — so the check here is an equality of form rather than a guess at intent.
/// </summary>
public sealed class APointerThatNamesAPathIsNotAPointerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-pointer-shape-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("generations")]
    [InlineData("")]
    [InlineData("   ")]
    // Right alphabet, wrong length: a truncated id names no generation either.
    [InlineData("0123456789abcdef")]
    // Right length and alphabet, wrong case. The producer lowercases, so an uppercase id was
    // written by something else, and on a case-insensitive file system it would silently resolve
    // to a directory this product did not name.
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void A_pointer_whose_generation_is_not_an_id_reads_as_unreadable(string generationId)
    {
        var runtimeRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(
            Path.Combine(runtimeRoot, "current.json"),
            JsonSerializer.Serialize(
                new GenerationPointer(generationId, "tree", DateTimeOffset.UnixEpoch),
                LocalAiJson.Strict));
        var store = new GenerationStore(FsPath.From(runtimeRoot));

        // Unreadable rather than absent, which is the distinction GenerationRetention needs: a
        // pointer nobody can make sense of is not permission to prune down to the count.
        Assert.False(store.TryReadCurrent(out var current));
        Assert.Null(current);
        Assert.Null(store.ReadCurrent());
    }

    /// <summary>
    /// And the shape a publish actually writes still reads, so the check refuses malformed
    /// documents rather than the product's own.
    /// </summary>
    [Fact]
    public void A_pointer_a_publish_wrote_still_reads()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));
        var published = store.PublishIndex(source, GenerationStoreTests.Identity());

        store.SetCurrent(published);

        Assert.True(store.TryReadCurrent(out var current));
        Assert.Equal(published.Identity.Id, current?.GenerationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
