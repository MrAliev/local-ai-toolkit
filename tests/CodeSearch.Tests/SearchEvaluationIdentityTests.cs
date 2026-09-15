using CodeSearch.Core.Chunking;
using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public sealed class SearchEvaluationIdentityTests
{
    [Theory]
    [InlineData("Thing.Run [1/6]", 1)]
    [InlineData("Thing.Run [2/6]", 1)]
    [InlineData("Thing.Run [6/6]", 1)]
    [InlineData("Thing.Run", 1)]
    [InlineData("Thing.Run [0/6]", 0)]
    [InlineData("Thing.Run [7/6]", 0)]
    [InlineData("Thing.Run [01/6]", 0)]
    [InlineData("Thing.Run [1/06]", 0)]
    [InlineData("Thing.Run [1/6] extra", 0)]
    [InlineData("Thing.Run [1/6] [2/6]", 0)]
    [InlineData("Thing.Run[1/6]", 0)]
    [InlineData("Thing.Run [1 /6]", 0)]
    [InlineData("Thing.Run [1/2147483648]", 0)]
    [InlineData("Other.Run [1/6]", 0)]
    public void Only_exact_part_suffixes_match_the_complete_symbol(string display, double recall)
    {
        var corpus = Corpus(new("Thing.cs", "Thing.Run"));
        var metrics = Measure(corpus, Hit("Thing.cs", display, 1, 10));
        Assert.Equal(recall, metrics.RecallAt10);
    }

    [Fact]
    public void Multiple_parts_count_once_and_another_file_does_not_match()
    {
        var corpus = Corpus(new("src/Thing.cs", "Thing.Run"));
        var metrics = Measure(corpus,
            Hit("other/Thing.cs", "Thing.Run [1/6]", 1, 10),
            Hit("src\\Thing.cs", "Thing.Run [1/6]", 1, 10),
            Hit("src/Thing.cs", "Thing.Run [2/6]", 8, 20));
        Assert.Equal(1, metrics.RecallAt10);
        Assert.Equal(0.2, metrics.PrecisionAt5);
        Assert.Equal(2, metrics.MeanFirstRelevantRank);
    }

    [Theory]
    [InlineData("class Thing { void Run(int x) {} void Run(string x) {} }")]
    [InlineData("namespace A { class Thing { void Run() {} } } namespace B { class Thing { void Run() {} } }")]
    [InlineData("// Thing.Run is only a comment\nclass Other { void Run() {} }")]
    public void Source_validation_rejects_ambiguous_or_nonexistent_declarations(string source)
    {
        InRepository(source, root => Assert.Throws<InvalidDataException>(() =>
            SearchEvaluationCorpus.ValidateAgainstSource(Corpus(new("Thing.cs", "Thing.Run")), root)));
    }

    [Theory]
    [InlineData("exact-codesearch-sync")]
    [InlineData("intent-broker-embeddings")]
    public void Actual_regression_cases_recognize_their_real_split_chunks(string caseId)
    {
        var root = RepositoryRoot();
        var corpus = SearchEvaluationCorpus.Load(Path.Combine(root,
            "tests/CodeSearch.Tests/Fixtures/SearchEvaluation/cases.json"));
        corpus = SearchEvaluationCorpus.ResolveAgainstSource(corpus, root);
        var item = corpus.Cases.Single(item => item.Id == caseId);
        var target = Assert.Single(item.Relevant);
        var chunks = new RoslynChunker().Split(target.Path,
            File.ReadAllText(Path.Combine(root, target.Path)))
            .Where(chunk => chunk.Symbol.StartsWith(target.Symbol + " [", StringComparison.Ordinal))
            .Take(2).ToArray();
        Assert.Equal(2, chunks.Length);
        var hits = chunks.Select(chunk => SearchEvaluation.FromSearchHit(new SearchHit(
            chunk.RelPath, chunk.StartLine, chunk.EndLine, chunk.Kind, chunk.Symbol,
            chunk.Signature, chunk.Namespace, 0.75f, 1, 1, "snippet"))).ToArray();
        var metrics = SearchEvaluation.Measure(new(1, [item]), [new(item.Id, hits, TimeSpan.Zero, null)]);
        Assert.Equal(1, metrics.RecallAt10);
        Assert.Equal(0.2, metrics.PrecisionAt5);
    }

    [Fact]
    public void Qualified_target_rejects_other_overloads_namespaces_types_and_source_ranges()
    {
        const string source = """
            namespace A
            {
                class Thing
                {
                    public void Run(int value) { }
                    public void Run(string value) { }
                }
                class Other { public void Run(int value) { } }
            }
            namespace B { class Thing { public void Run(int value) { } } }
            """;
        InRepository(source, root =>
        {
            var corpus = SearchEvaluationCorpus.ResolveAgainstSource(
                Corpus(new("Thing.cs", "Thing.Run", "public void Run(int value)", "A")), root);
            var chunks = new RoslynChunker().Split("Thing.cs", source).Where(chunk => chunk.Kind == ChunkKind.Method).ToArray();
            var expected = chunks.Single(chunk => chunk.Namespace == "A" && chunk.Symbol == "Thing.Run" &&
                chunk.Signature == "public void Run(int value)");
            foreach (var unrelated in chunks.Where(chunk => chunk != expected))
                Assert.Equal(0, Measure(corpus, EvaluationHit(unrelated)).RecallAt10);

            var correct = EvaluationHit(expected);
            Assert.Equal(1, Measure(corpus, correct).RecallAt10);
            Assert.Equal(0, Measure(corpus, correct with { StartLine = 50, EndLine = 60 }).RecallAt10);
            Assert.Equal(0, Measure(corpus, correct with { Kind = ChunkKind.Type }).RecallAt10);
            Assert.Equal(0, Measure(corpus, correct with { Signature = null }).RecallAt10);
            Assert.Equal(0, Measure(corpus, correct with { Namespace = null }).RecallAt10);
        });
    }

    [Fact]
    public void Split_overloads_are_resolved_separately_and_all_parts_of_the_selected_one_match()
    {
        var body = string.Join('\n', Enumerable.Repeat("        System.Console.WriteLine(value);", 300));
        var source = "namespace A;\nclass Thing\n{\n    public void Run(int value)\n    {\n" + body +
            "\n    }\n    public void Run(string value)\n    {\n" + body + "\n    }\n}";
        InRepository(source, root =>
        {
            var corpus = SearchEvaluationCorpus.ResolveAgainstSource(
                Corpus(new("Thing.cs", "Thing.Run", "public void Run(int value)", "A")), root);
            var chunks = new RoslynChunker().Split("Thing.cs", source).Where(chunk => chunk.Kind == ChunkKind.Method).ToArray();
            Assert.True(chunks.Length >= 4);
            foreach (var chunk in chunks)
                Assert.Equal(chunk.Signature.Contains("int value", StringComparison.Ordinal) ? 1 : 0,
                    Measure(corpus, EvaluationHit(chunk)).RecallAt10);
            Assert.Equal(0.2, Measure(corpus, chunks.Take(2).Select(EvaluationHit).ToArray()).PrecisionAt5);
        });
    }

    [Fact]
    public void Truncated_signature_collisions_fail_closed_instead_of_merging_overloads()
    {
        var parameter = "int " + new string('a', 410);
        var source = "class Thing { void Run(" + parameter + ", int other) {} void Run(" +
            parameter + ", string other) {} }";
        InRepository(source, root =>
        {
            var chunks = new RoslynChunker().Split("Thing.cs", source).Where(chunk => chunk.Kind == ChunkKind.Method).ToArray();
            Assert.Equal(chunks[0].Signature, chunks[1].Signature);
            Assert.Throws<InvalidDataException>(() => SearchEvaluationCorpus.ResolveAgainstSource(
                Corpus(new("Thing.cs", "Thing.Run", chunks[0].Signature, "")), root));
        });
    }

    [Fact]
    public void Optional_declaration_qualifiers_survive_loading_a_schema_one_corpus()
    {
        InRepository("namespace A; class Thing { public void Run(int value) {} public void Run(string value) {} }", root =>
        {
            var path = Path.Combine(root, "cases.json");
            File.WriteAllText(path, """
                {"schemaVersion":1,"cases":[{"id":"case","query":"query","category":"intent","noAnswer":false,
                  "relevant":[{"path":"Thing.cs","symbol":"Thing.Run","signature":"public void Run(int value)","namespace":"A"}]}]}
                """);
            var corpus = SearchEvaluationCorpus.ResolveAgainstSource(SearchEvaluationCorpus.Load(path), root);
            var target = Assert.Single(Assert.Single(corpus.Cases).Relevant);
            Assert.Equal("public void Run(int value)", target.Signature);
            Assert.Equal("A", target.Namespace);
        });
    }

    private static SearchEvaluationHit EvaluationHit(Chunk chunk) => SearchEvaluation.FromSearchHit(new(
        chunk.RelPath, chunk.StartLine, chunk.EndLine, chunk.Kind, chunk.Symbol,
        chunk.Signature, chunk.Namespace, 0.75f, 1, 1, "snippet"));

    private static SearchEvaluationCorpus Corpus(SearchEvaluationTarget target) =>
        new(1, [new("case", "query", "intent", false, [target])]);

    private static SearchEvaluationMetrics Measure(SearchEvaluationCorpus corpus, params SearchEvaluationHit[] hits) =>
        SearchEvaluation.Measure(corpus, [new("case", hits, TimeSpan.Zero, null)]);

    private static SearchEvaluationHit Hit(string path, string symbol, int start, int end) =>
        new(path, symbol, start, end, 20, 2);

    private static void InRepository(string source, Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "evaluation-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Thing.cs"), source);
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "LocalAi.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not locate LocalAi.slnx.");
    }
}
