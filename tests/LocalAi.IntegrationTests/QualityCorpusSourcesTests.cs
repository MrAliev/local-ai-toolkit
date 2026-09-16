using CodeSearch.Core.Search;
using LocalAi.TestSupport;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class QualityCorpusSourcesTests
{
    [Theory]
    [InlineData("csharp", ".cs")]
    [InlineData("typescript", ".ts")]
    [InlineData("python", ".py")]
    public void Each_language_has_real_targets_and_near_neighbours(string corpus, string extension)
    {
        var files = QualityCorpusSources.Files(corpus).ToArray();
        Assert.True(files.Count(file => file.Path.EndsWith(extension, StringComparison.Ordinal)) >= 10);
        Assert.DoesNotContain(files, file => file.Path.Contains("cases", StringComparison.OrdinalIgnoreCase) ||
            file.Path.Contains("baseline", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(files, QualityCorpusSources.Files(corpus).ToArray());
    }

    [Fact]
    public void Mixed_tree_preserves_all_language_targets_with_no_path_collisions()
    {
        var mixed = QualityCorpusSources.Files("mixed").ToDictionary(file => file.Path, file => file.Text);
        foreach (var corpus in new[] { "csharp", "typescript", "python" })
        {
            var sources = QualityCorpusSources.Files(corpus).Where(file => file.Path.EndsWith(".cs", StringComparison.Ordinal) ||
                file.Path.EndsWith(".ts", StringComparison.Ordinal) || file.Path.EndsWith(".tsx", StringComparison.Ordinal) ||
                file.Path.EndsWith(".py", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(sources);
            foreach (var (path, text) in sources) Assert.Equal(text, mixed[path]);
        }
    }

    [Fact]
    public void Csharp_annotations_resolve_to_actual_declarations_including_a_long_method()
    {
        using var directory = new TemporaryDirectory("localai-quality-source");
        QualityCorpusSources.Write(directory.Path, "csharp");
        var corpus = new SearchEvaluationCorpus(1,
        [
            new("weight", "weight-based shipping quote", "csharp", false,
                [new("src/pricing/WeightPricing.cs", "WeightPricing.Quote")]),
            new("manifest", "collect every missing manifest field", "csharp", false,
                [new("src/manifests/ManifestReader.cs", "ManifestReader.ValidateAllFields")])
        ]);
        var resolved = SearchEvaluationCorpus.ResolveAgainstSource(corpus, directory.Path);
        Assert.Equal(2, resolved.Cases.Count);
        Assert.True(File.ReadAllLines(Path.Combine(directory.Path, "src/manifests/ManifestReader.cs")).Length > 100);
    }
}
