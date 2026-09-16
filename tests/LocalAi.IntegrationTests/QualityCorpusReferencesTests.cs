using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Core.Search;
using LocalAi.Quality;
using LocalAi.TestSupport;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class QualityCorpusReferencesTests
{
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [InlineData("python")]
    [InlineData("mixed")]
    public void Search_references_are_bilingual_resolvable_and_outside_the_indexed_sources(string name)
    {
        var casesPath = Path.Combine(Root, "tests/CodeSearch.Tests/Fixtures/SearchEvaluation/independent", name + ".json");
        Assert.True(File.Exists(casesPath), "The independent reference corpus must be retained.");
        var corpus = SearchEvaluationCorpus.Load(casesPath);
        Assert.True(corpus.Cases.Count(item => !item.NoAnswer) >= 8);
        Assert.True(corpus.Cases.Count(item => item.NoAnswer) >= 2);
        Assert.Contains(corpus.Cases, item => item.Category.StartsWith("ru-", StringComparison.Ordinal));
        Assert.Contains(corpus.Cases, item => item.Category.StartsWith("en-", StringComparison.Ordinal));
        Assert.Equal(corpus.Cases.Count, corpus.Cases.Select(item => item.Id).Distinct().Count());

        using var source = new TemporaryDirectory("localai-independent-references");
        QualityCorpusSources.Write(source.Path, name);
        var resolved = SearchEvaluationCorpus.ResolveAgainstSource(corpus, source.Path);
        Assert.Equal(corpus.Cases.Count, resolved.Cases.Count);
        var files = QualityCorpusSources.Files(name).ToArray();
        foreach (var item in corpus.Cases)
        {
            Assert.DoesNotContain(files, file => file.Text.Contains(item.Query, StringComparison.Ordinal));
            if (item.NoAnswer) Assert.Empty(item.Relevant);
            else Assert.All(item.Relevant, target => Assert.False(string.IsNullOrWhiteSpace(target.Symbol)));
        }
        Assert.DoesNotContain(Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("cases", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ocr_references_have_verified_images_fields_and_unclipped_line_geometry()
    {
        var directory = Path.Combine(Root, "tests/Fixtures/OcrQuality");
        var path = Path.Combine(directory, "corpus.json");
        Assert.True(File.Exists(path), "The visual and textual OCR references must be retained.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var cases = manifest.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(4, cases.Length);
        Assert.Equal(4, cases.Select(item => (item.GetProperty("language").GetString(), item.GetProperty("length").GetString())).Distinct().Count());
        foreach (var item in cases)
        {
            var imageBytes = File.ReadAllBytes(Path.Combine(directory, item.GetProperty("image").GetString()!));
            var referenceBytes = File.ReadAllBytes(Path.Combine(directory, item.GetProperty("reference").GetString()!));
            Assert.Equal(item.GetProperty("imageSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(imageBytes)));
            Assert.Equal(item.GetProperty("referenceSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(referenceBytes)));
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, imageBytes[..8]);
            var width = BinaryPrimitives.ReadInt32BigEndian(imageBytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(imageBytes.AsSpan(20, 4));
            Assert.Equal(item.GetProperty("width").GetInt32(), width);
            Assert.Equal(item.GetProperty("height").GetInt32(), height);
            var reference = System.Text.Encoding.UTF8.GetString(referenceBytes);
            var fields = item.GetProperty("fields").EnumerateArray().Select(field =>
                new OcrFieldReference(field.GetProperty("label").GetString()!, field.GetProperty("value").GetString()!)).ToArray();
            var score = OcrQualityScoring.Measure(reference, reference, true, fields, 0);
            Assert.Equal(0, score.CharacterErrorRate);
            Assert.Equal(1, score.FieldValueAccuracy);
            var boxes = item.GetProperty("lineBoxes").EnumerateArray().ToArray();
            Assert.Equal(score.ReferenceLineCount, boxes.Length);
            var previousBottom = 0;
            foreach (var entry in boxes)
            {
                var box = entry.GetProperty("box").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                Assert.InRange(box[0], 0, width - 1);
                Assert.InRange(box[2], box[0] + 1, width);
                Assert.InRange(box[1], previousBottom, height - 1);
                Assert.InRange(box[3], box[1] + 1, height);
                previousBottom = box[3];
            }
        }
        var repeated = File.ReadAllLines(Path.Combine(directory, "short-repeated-en.txt"));
        Assert.Equal(new[] { "STAMP: OK", "STAMP: OK" }, repeated);
    }

    [Fact]
    public void Retained_baseline_matches_reference_hashes_and_recomputes_the_ocr_scores()
    {
        var references = Path.Combine(Root, "tests/CodeSearch.Tests/Fixtures/SearchEvaluation/independent");
        var baseline = Path.Combine(references, "baseline-2026-09-16");
        Assert.True(Directory.Exists(baseline), "The measured baseline must be retained with its provenance.");
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "run.json")));
        Assert.True(run.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal(0, run.RootElement.GetProperty("sourceStatus").GetArrayLength());
        foreach (var name in new[] { "csharp", "typescript", "python", "mixed" })
        {
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, name + ".json")));
            var data = result.RootElement;
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(references, name + ".json")))),
                data.GetProperty("corpusSha256").GetString());
            Assert.Equal(data.GetProperty("sourceCommit").GetString(), data.GetProperty("indexedCommit").GetString());
            Assert.Equal(data.GetProperty("sourceTree").GetString(), data.GetProperty("indexedTree").GetString());
            Assert.True(string.IsNullOrEmpty(data.GetProperty("dirtyHash").GetString()));
            Assert.Equal(SearchEvaluationCorpus.Load(Path.Combine(references, name + ".json")).Cases.Count,
                data.GetProperty("observations").GetArrayLength());
            var sourcePaths = QualityCorpusSources.Files(name).Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var observation in data.GetProperty("observations").EnumerateArray())
                foreach (var hit in observation.GetProperty("hits").EnumerateArray())
                    Assert.Contains(hit.GetProperty("path").GetString()!.Replace('\\', '/'), sourcePaths);
        }

        var fixtures = Path.Combine(Root, "tests/Fixtures/OcrQuality");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "corpus.json")));
        var cases = manifest.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
        using var ocr = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "ocr.json")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixtures, "corpus.json")))),
            ocr.RootElement.GetProperty("corpusSha256").GetString());
        Assert.Equal(cases.Count, ocr.RootElement.GetProperty("cases").GetArrayLength());
        foreach (var result in ocr.RootElement.GetProperty("cases").EnumerateArray())
        {
            var item = cases[result.GetProperty("id").GetString()!];
            Assert.Equal(item.GetProperty("imageSha256").GetString(), result.GetProperty("imageSha256").GetString());
            Assert.Equal(item.GetProperty("referenceSha256").GetString(), result.GetProperty("referenceSha256").GetString());
            var fields = item.GetProperty("fields").EnumerateArray().Select(field =>
                new OcrFieldReference(field.GetProperty("label").GetString()!, field.GetProperty("value").GetString()!)).ToArray();
            var recorded = JsonSerializer.Deserialize<OcrQualityScore>(result.GetProperty("score").GetRawText())!;
            var actual = result.GetProperty("response").GetProperty("data");
            Assert.False(string.IsNullOrWhiteSpace(actual.GetProperty("model").GetString()));
            Assert.Equal(recorded, OcrQualityScoring.Measure(
                File.ReadAllText(Path.Combine(fixtures, item.GetProperty("reference").GetString()!)),
                actual.GetProperty("answer").GetString(), recorded.TechnicalCompletion, fields, recorded.ElapsedMilliseconds));
        }
    }

    private static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("The repository root was not found.");
        }
    }
}
