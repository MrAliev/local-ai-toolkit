using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// The lines a person actually watches during an index build.
///
/// Everything else on the sync path already follows the reader: the refusals through IndexText,
/// the console's own sentences and the retention summary through CliText. The narration of the
/// build was the hole, so a Russian machine printed its refusals and its final line in Russian and
/// its progress in English - and the progress is the half that answers "how far along is this and
/// how long do I wait".
///
/// The `codesearch` binary pins English for both its faces on purpose, so these lines are asserted
/// through IndexBuilder itself rather than through a command line: `localai sync` is the entry
/// point that follows the reader, and it is the one a person watches.
/// </summary>
public sealed class BuildNarrationFollowsTheReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-narration-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The fact worth stating before any assertion about a particular line: the narration is not
    /// the same text in both languages. A catalogue that fell back to English everywhere would
    /// pass every other test in this file.
    /// </summary>
    [Fact]
    public async Task The_narration_is_not_the_same_text_in_both_languages()
    {
        Sources(2);

        var english = await ColdBuild();
        string[] russian;
        using (TestCulture.Reading("ru"))
        {
            russian = await ColdBuild();
        }

        Assert.NotEqual(english, russian);
    }

    /// <summary>
    /// The one the issue is named for. It prints once per completed batch, which on a large
    /// repository is every fifteen seconds for the better part of an hour, and it is the only
    /// progress a person sees on a hook run.
    /// </summary>
    [Fact]
    public async Task The_line_a_person_actually_watches_moves_with_the_language()
    {
        Sources(2);

        var english = await ColdBuild();
        string[] russian;
        using (TestCulture.Reading("ru"))
        {
            russian = await ColdBuild();
        }

        Assert.Contains(english, line => line.Contains("Embedded ", StringComparison.Ordinal));
        Assert.DoesNotContain(russian, line => line.Contains("Embedded ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A second build over the same index reaches the line that reports what it is reusing. It is
    /// the sentence that explains why a run took seconds rather than an hour, and it prints on
    /// every incremental sync.
    /// </summary>
    [Fact]
    public async Task The_line_reporting_a_reused_index_moves_with_the_language()
    {
        Sources(2);
        await ColdBuild();

        using var reading = TestCulture.Reading("ru");
        var second = await Narrate();

        Assert.Contains(
            second,
            line => line.Contains(IndexText.BuildReusingIndex(2, 2), StringComparison.Ordinal));
    }

    /// <summary>
    /// What the line is about does not move. The path is what a reader compares against the
    /// repository they are standing in, and a translated one compares equal to nothing.
    /// </summary>
    [Fact]
    public async Task The_path_a_line_names_does_not_move_with_the_language()
    {
        Sources(2);

        using var reading = TestCulture.Reading("ru");
        var russian = await ColdBuild();

        Assert.Contains(IndexText.BuildScanned(_root, 2), russian);
        Assert.Contains(russian, line => line.Contains(Index, StringComparison.Ordinal));
    }

    private const string Index = "narration.cidx";

    /// <summary>
    /// The same index path every time, and never a reused one: two runs writing to two different
    /// files differ in the line naming the file, which would let this pass with no translation at
    /// all.
    /// </summary>
    private async Task<string[]> ColdBuild()
    {
        var path = Path.Combine(_root, Index);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return await Narrate();
    }

    private async Task<string[]> Narrate()
    {
        var lines = new List<string>();
        await new IndexBuilder(new StubEmbedder(), lines.Add).BuildAsync(
            _root,
            Path.Combine(_root, Index),
            ct: TestContext.Current.CancellationToken);
        return [.. lines];
    }

    private void Sources(int count)
    {
        Directory.CreateDirectory(_root);
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(
                Path.Combine(_root, $"File{index}.cs"),
                $"public sealed class File{index} {{ public int Value => {index}; }}");
        }
    }

    private sealed class StubEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
