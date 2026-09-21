using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class TokenCalibrationTests
{
    [Fact]
    public void Runtime_tokenizer_matches_independently_retained_o200k_counts()
    {
        using var corpus = Document("corpus.json");
        using var reference = Document("reference.json");
        var texts = Texts(corpus.RootElement);
        foreach (var sample in reference.RootElement.GetProperty("texts").EnumerateArray())
        {
            var text = texts[sample.GetProperty("id").GetString()!];
            Assert.Equal(UsageValue.Measured(sample.GetProperty("tokens").GetProperty("o200k_base").GetInt64()),
                O200kCloudTokenProfile.Instance.CountText(text));
        }
    }

    private static readonly JsonSerializerOptions ArgumentsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void The_retained_measurements_reproduce_the_character_heuristic_and_its_reference_errors()
    {
        using var corpus = Document("corpus.json");
        using var reference = Document("reference.json");
        var texts = Texts(corpus.RootElement);
        Assert.Equal("0.12.0", reference.RootElement.GetProperty("tiktokenVersion").GetString());
        Assert.Equal("heuristic_not_confidence_interval", reference.RootElement.GetProperty("rangeBasis").GetString());
        foreach (var sample in reference.RootElement.GetProperty("texts").EnumerateArray())
        {
            var text = texts[sample.GetProperty("id").GetString()!];
            Assert.Equal(Hash(text), sample.GetProperty("sha256").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount(text), sample.GetProperty("utf8Bytes").GetInt32());
            Assert.Equal(text.Length, sample.GetProperty("utf16Characters").GetInt32());
            Assert.Equal(text.Count(char.IsLetter), sample.GetProperty("letters").GetInt32());
            Assert.Equal(text.Count(ch => char.IsLetter(ch) && ch is >= 'Ѐ' and <= 'ӿ'), sample.GetProperty("cyrillicLetters").GetInt32());
            var estimate = HeuristicCloudTokenProfile.Instance.CountText(text);
            Assert.Equal(sample.GetProperty("weightedEstimate").GetInt32(), estimate.Value);
            Assert.Null(estimate.Range);
            if (text.Length > 0) Assert.Equal("not_calibrated", estimate.RangeReason);
        }
        foreach (var encoding in new[] { "cl100k_base", "o200k_base" })
        {
            var cases = reference.RootElement.GetProperty("texts").EnumerateArray().ToArray();
            var summary = reference.RootElement.GetProperty("summary").GetProperty(encoding);
            Assert.Equal(cases.Length, summary.GetProperty("cases").GetInt32());
            Assert.Equal(cases.Sum(item => Math.Abs(HeuristicCloudTokenProfile.EstimateText(texts[item.GetProperty("id").GetString()!])
                - item.GetProperty("tokens").GetProperty(encoding).GetInt32())), summary.GetProperty("weightedAbsoluteErrorSum").GetInt32());
            Assert.Equal(cases.Where(item => item.GetProperty("tokens").GetProperty(encoding).GetInt32() < item.GetProperty("heuristicLower").GetInt32()
                || item.GetProperty("tokens").GetProperty(encoding).GetInt32() > item.GetProperty("heuristicUpper").GetInt32())
                .Select(item => item.GetProperty("id").GetString()), summary.GetProperty("characterRangeMisses").EnumerateArray().Select(item => item.GetString()));
        }
        // These counts come from the independent tokenizer run, not from the character proxy.
        var russian = reference.RootElement.GetProperty("texts").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "russian");
        Assert.Equal(98, russian.GetProperty("tokens").GetProperty("cl100k_base").GetInt32());
        Assert.Equal(54, russian.GetProperty("tokens").GetProperty("o200k_base").GetInt32());
        Assert.True(russian.GetProperty("tokens").GetProperty("cl100k_base").GetInt32() > russian.GetProperty("heuristicUpper").GetInt32());
    }

    [Theory]
    [InlineData("cl100k_base")]
    [InlineData("o200k_base")]
    public void Independent_token_counts_cover_source_path_file_delivery_and_repeated_reading(string encoding)
    {
        using var corpus = Document("corpus.json");
        using var reference = Document("reference.json");
        var texts = Texts(corpus.RootElement);
        var profile = new ReferenceProfile(reference.RootElement, encoding);
        foreach (var item in corpus.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            var scenarioId = item.GetProperty("id").GetString()!;
            var source = texts[item.GetProperty("source").GetString()!];
            var output = texts[item.GetProperty("output").GetString()!];
            var toFile = item.GetProperty("delivery").GetString() == "file";
            var arguments = item.GetProperty("arguments").GetString() == "source"
                ? new Dictionary<string, string> { ["source"] = source } : new Dictionary<string, string> { ["path"] = "input.txt" };
            if (toFile) arguments["outPath"] = "output.txt";
            var argumentText = JsonSerializer.Serialize(arguments, ArgumentsJson);
            var extraInput = argumentText + (item.GetProperty("readOutput").GetBoolean() ? "\n" + output : "");
            var payload = texts["notice"] + "\n" + (toFile ? "Saved output.txt."
                : "<untrusted-content id=\"fixture\">" + output + "</untrusted-content id=\"fixture\">");
            var presence = Enum.Parse<CloudContextPresence>(item.GetProperty("presence").GetString()!);
            var sources = Enumerable.Range(0, item.GetProperty("copies").GetInt32())
                .Select(_ => (CloudSource)new CloudTextSource("synthetic-snapshot", source, 0, presence)).ToArray();
            var scenario = new CloudTokenScenario(scenarioId, sources, output, extraInput, argumentText);
            var result = CloudTokenMethodology.Apply(Report(), scenario, payload, profile);
            var expected = reference.RootElement.GetProperty("scenarios").EnumerateArray()
                .Single(entry => entry.GetProperty("id").GetString() == scenarioId).GetProperty("expected").GetProperty(encoding);
            foreach (var key in Enum.GetValues<UsageCloudTokens>())
            {
                var value = expected.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(key.ToString()));
                if (value.ValueKind == JsonValueKind.Null)
                    Assert.Equal(UsageValue.Unknown("source_context_unknown"), result.CloudTokens[key]);
                else Assert.Equal(UsageValue.Measured(value.GetInt64()), result.CloudTokens[key]);
            }
            Assert.Equal(Encoding.UTF8.GetByteCount(payload), result.Volumes[UsageVolume.ResponseBytes].Value);
            var persisted = UsageReportJson.Write(result);
            var restored = UsageReportJson.Read(persisted);
            Assert.Equal(result.CloudTokens[UsageCloudTokens.InputSaved], restored.CloudTokens[UsageCloudTokens.InputSaved]);
            using var parsed = JsonDocument.Parse(persisted);
            Assert.DoesNotContain(Strings(parsed.RootElement), text => text.Contains(source, StringComparison.Ordinal)
                || text.Contains(output, StringComparison.Ordinal) || text == argumentText || text == payload);

            var undelivered = Report();
            undelivered = undelivered with { Outcome = undelivered.Outcome with { Delivery = UsageDelivery.Failed, DeliveryReason = "output_not_written" } };
            var failed = CloudTokenMethodology.Apply(undelivered, scenario, payload, profile);
            Assert.Equal(UsageValue.Unknown("result_not_delivered"), failed.CloudTokens[UsageCloudTokens.OutputSaved]);
        }
    }

    [Fact]
    public void Real_image_and_pdf_fixtures_do_not_turn_an_unsupported_vision_profile_into_an_exact_count()
    {
        using var corpus = Document("corpus.json");
        using var reference = Document("reference.json");
        foreach (var image in reference.RootElement.GetProperty("images").EnumerateArray())
        {
            var bytes = Resource(image.GetProperty("path").GetString()!);
            Assert.Equal(image.GetProperty("sha256").GetString(), Hash(bytes));
            Assert.Equal(image.GetProperty("width").GetInt32(), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
            Assert.Equal(image.GetProperty("height").GetInt32(), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
            Assert.Equal("published_formula_estimate", image.GetProperty("referenceStatus").GetString());
            var source = new CloudImageSource("image", image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(),
                image.GetProperty("detail").GetString()!, CloudContextPresence.NotPresent);
            var report = CloudTokenMethodology.Apply(Report(), new("image-case", [source], "", "", ""), "", HeuristicCloudTokenProfile.Instance);
            Assert.Equal(UsageValue.Unknown("vision_profile_unknown"), report.CloudTokens[UsageCloudTokens.InputSaved]);
        }
        var pdf = corpus.RootElement.GetProperty("pdf");
        var pdfReference = reference.RootElement.GetProperty("pdf");
        Assert.Equal(pdfReference.GetProperty("sha256").GetString(), Hash(Resource(pdf.GetProperty("path").GetString()!)));
        Assert.Equal(JsonValueKind.Null, pdfReference.GetProperty("fullInputTokens").ValueKind);
        Assert.Equal("provider_page_rendering_unknown", pdfReference.GetProperty("reason").GetString());
        var pages = pdf.GetProperty("pages").EnumerateArray().SelectMany((page, index) => new CloudSource[]
        {
            new CloudTextSource("page-text-" + index, page.GetString()!, 0, CloudContextPresence.NotPresent),
            new CloudImageSource("page-image-" + index, 612, 792, "high", CloudContextPresence.NotPresent),
        }).ToArray();
        var comparison = CloudTokenMethodology.Apply(Report(), new("pdf-case", pages, "", "", ""), "", HeuristicCloudTokenProfile.Instance);
        Assert.Equal(UsageValue.Unknown("vision_profile_unknown"), comparison.CloudTokens[UsageCloudTokens.InputSaved]);
    }

    private static UsageReport Report()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        return call.Complete();
    }
    private static Dictionary<string, string> Texts(JsonElement corpus)
    {
        var result = new Dictionary<string, string>();
        foreach (var item in corpus.GetProperty("texts").EnumerateArray())
        {
            var unit = item.TryGetProperty("text", out var text) ? text.GetString()!
                : string.Concat(item.GetProperty("parts").EnumerateArray().Select(part => result[part.GetString()!] + "\n"));
            result[item.GetProperty("id").GetString()!] = string.Concat(Enumerable.Repeat(unit, item.GetProperty("repeat").GetInt32()));
        }
        return result;
    }
    private static JsonDocument Document(string name) => JsonDocument.Parse(Resource(name));
    private static byte[] Resource(string name)
    {
        using var stream = typeof(TokenCalibrationTests).Assembly.GetManifestResourceStream("TokenCalibration." + name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => Strings(property.Value)),
        _ => [],
    };
    // Independent, retained tokenizer measurements are the oracle. An unseen text is an
    // error in this fixture, never a made-up estimate or a production tokenizer adapter.
    private sealed class ReferenceProfile(JsonElement reference, string encoding) : ICloudTokenProfile
    {
        public string TextProfileId => "tiktoken-0.12.0:" + encoding;
        public string? VisionProfileId => null;
        public UsageValue CountText(string text) => UsageValue.Measured(reference.GetProperty("observations")
            .GetProperty(Hash(text)).GetProperty("tokens").GetProperty(encoding).GetInt64());
        public UsageValue CountImage(CloudImageSource image) => UsageValue.Unknown("vision_profile_unknown");
    }
}
