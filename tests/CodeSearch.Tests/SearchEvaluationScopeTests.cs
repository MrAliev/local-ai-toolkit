using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public sealed class SearchEvaluationScopeTests
{
    [Fact]
    public void Scope_excludes_its_own_corpus_and_explicit_reference_material_without_excluding_all_tests()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evaluation-scope"));
        var corpus = new SearchEvaluationCorpus(1, [new("test", "query", "test", false,
            [new("tests/UsefulTests.cs", null)])], [@"references\", "references/cases.json"]);
        var scope = SearchEvaluationScope.Create(root, Path.Combine(root, "evaluation/cases.json"), corpus);
        Assert.Contains("evaluation/cases.json", scope.ExcludedPaths);
        Assert.Contains("references/", scope.ExcludedPaths);
        Assert.DoesNotContain("tests/", scope.ExcludedPaths);
    }

    [Theory]
    [InlineData("../outside/")]
    [InlineData("/absolute/")]
    [InlineData("C:\\absolute")]
    [InlineData("./")]
    [InlineData("tests/../")]
    [InlineData("tests/**/cases.json")]
    [InlineData("")]
    public void Scope_rejects_non_literal_or_escaping_exclusion_paths(string exclusion)
    {
        var root = Path.GetTempPath();
        var corpus = new SearchEvaluationCorpus(1, [new("none", "query", "test", true, [])], [exclusion]);
        Assert.Throws<InvalidDataException>(() => SearchEvaluationScope.Create(root,
            Path.Combine(root, "cases.json"), corpus));
    }

    [Fact]
    public void Scope_rejects_a_target_hidden_by_explicit_or_automatic_exclusions()
    {
        var root = Path.GetTempPath();
        var corpus = new SearchEvaluationCorpus(1, [new("test", "query", "test", false,
            [new("references/answer.cs", "Answer")])], ["references/"]);
        Assert.Throws<InvalidDataException>(() => SearchEvaluationScope.Create(root,
            Path.Combine(root, "cases.json"), corpus));
        var self = corpus with { ExcludedPaths = [], Cases = [new("self", "query", "test", false, [new("cases.json", null)])] };
        Assert.Throws<InvalidDataException>(() => SearchEvaluationScope.Create(root,
            Path.Combine(root, "cases.json"), self));
    }

    [Fact]
    public void External_corpus_is_not_mistaken_for_a_file_in_a_similarly_named_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var corpus = new SearchEvaluationCorpus(1, [new("none", "query", "test", true, [])]);
        var scope = SearchEvaluationScope.Create(root, Path.Combine(root + "-other", "cases.json"), corpus);
        Assert.Empty(scope.ExcludedPaths);
    }

    [Fact]
    public void Committed_scope_covers_both_reference_corpora_and_keeps_ordinary_tests_searchable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LocalAi.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        const string relative = "tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json";
        var path = Path.Combine(root.FullName, relative);
        var scope = SearchEvaluationScope.Create(root.FullName, path, SearchEvaluationCorpus.Load(path));
        var index = Index([relative, "tests/CodeSearch.Tests/Fixtures/SearchEvaluation/synthetic-frontend-cases.json",
            "tests/CodeSearch.Tests/SearchEngineTests.cs"], _ => "Needle", "");
        var hit = Assert.Single(SearchEngine.SearchLexically(index, "Needle",
            new SearchOptions { ExcludedPaths = scope.ExcludedPaths }, root.FullName));
        Assert.Equal("tests/CodeSearch.Tests/SearchEngineTests.cs", hit.RelPath);
        Assert.Equal(64, scope.Sha256.Length);
        Assert.Equal(scope.Sha256, SearchEvaluationScope.Create(root.FullName, path,
            SearchEvaluationCorpus.Load(path)).Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exclusions_run_before_signal_candidate_limits_and_preserve_useful_tests(bool lexicalOnly)
    {
        // More reference hits than either signal's 250-candidate limit. Filtering the retrieved
        // top ten, or even filtering after signal ranking, loses the useful answer permanently.
        var paths = Enumerable.Range(0, 300).Select(i => $"references/{i}.json")
            .Append("tests/UsefulTests.cs").ToArray();
        var index = Index(paths, i => i < 300 ? "Needle" : "UsefulTests", "Needle");
        var options = new SearchOptions { TopK = 10, ExcludedPaths = ["references/"] };
        var hits = lexicalOnly
            ? SearchEngine.SearchLexically(index, "Needle", options, Path.GetTempPath())
            : SearchEngine.Search(index, [1f, 0f], "Needle", options, Path.GetTempPath());
        Assert.Equal("tests/UsefulTests.cs", Assert.Single(hits).RelPath);
    }

    [Fact]
    public void Negative_case_cannot_retrieve_its_own_question_but_normal_search_keeps_it()
    {
        var index = Index(["references/cases.json", "tests/UsefulTests.cs"],
            i => i == 0 ? "NonexistentFeature" : "UsefulTests", "");
        var regular = new SearchOptions { MinVectorScore = 0.9f };
        var scope = regular with { ExcludedPaths = ["references/cases.json"] };
        Assert.Equal("references/cases.json", Assert.Single(SearchEngine.Search(
            index, [1f, 0f], "NonexistentFeature", regular, Path.GetTempPath())).RelPath);
        Assert.Empty(SearchEngine.Search(index, [1f, 0f], "NonexistentFeature", scope, Path.GetTempPath()));
        Assert.Empty(SearchEngine.SearchLexically(index, "NonexistentFeature", scope, Path.GetTempPath()));
    }

    [Fact]
    public void Exclusions_normalize_separators_and_match_directory_boundaries_and_exact_files()
    {
        var index = Index([@"references\cases.json", "references-other/test.cs", "tests/Useful.cs",
            "tests/Useful.cs.bak"], _ => "Needle", "");
        var hits = SearchEngine.SearchLexically(index, "Needle",
            new SearchOptions { ExcludedPaths = ["references/", @"tests\Useful.cs"] }, Path.GetTempPath());
        Assert.Equal(["references-other/test.cs", "tests/Useful.cs.bak"], hits.Select(hit => hit.RelPath));
    }

    private static CodeIndex Index(string[] paths, Func<int, string> symbol, string lexicalText) => new()
    {
        Dim = 2, Model = "test", Root = Path.GetTempPath(), GitCommit = "commit",
        RepositoryId = "repository", GenerationId = "generation", GitTree = "tree", IndexedAtUtc = DateTime.UnixEpoch,
        Files = paths.Select((path, i) => new IndexedFile
        { RelPath = path, Hash = new byte[32], ChunkStart = i, ChunkCount = 1 }).ToList(),
        Chunks = paths.Select((_, i) => new ChunkMeta
        {
            FileIndex = i, Kind = ChunkKind.Text, Symbol = symbol(i), Signature = "", Namespace = "",
            LexicalText = lexicalText, StartLine = 1, EndLine = 1
        }).ToList(),
        Vectors = paths.SelectMany((_, i) => i == paths.Length - 1 ? new[] { 0.6f, 0.8f } : new[] { 1f, 0f }).ToArray()
    };
}
