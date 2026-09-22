using LocalAi.Contracts;
using LocalAi.Cli;
using System.Text.Json;
using LocalLm.Core;

namespace LocalAi.IntegrationTests;

public sealed class StructuredOutputValidationTests
{
    [Theory]
    [InlineData("{\"label\":\"bug\",\"count\":1,\"verified\":true}", 0, true)]
    [InlineData("{\"label\":false}", 65, true)]
    [InlineData("```json\n{\"label\":\"bug\"}\n```", 65, true)]
    [InlineData("{\"label\":\"bug\",\"count\":1,\"verified\":true}", 0, false)]
    [InlineData("{\"label\":false}", 65, false)]
    public async Task Cli_delivery_status_matches_validation_and_does_not_deliver_rejected_model_text(string answer, int expectedExit, bool machineReadable)
    {
        var result = new LocalResult(answer, 0, "test", "test", new LocalUsageReceipt(Guid.NewGuid(), "local-lm", "chat", "test",
            TimeSpan.Zero, TimeSpan.Zero, 10, 2, null, null, null))
        { Validation = StructuredOutputValidator.Validate(answer, Contract(), TestContext.Current.CancellationToken) };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await LocalModelRun.ExecuteAsync("ask", "test", LocalTaskProfile.Extraction,
            (_, _) => Task.FromResult(result), machineReadable, TestContext.Current.CancellationToken, new CommandStreams(output, error));
        Assert.Equal(expectedExit, exit);
        if (!machineReadable)
        {
            if (expectedExit == 0)
            {
                Assert.Contains("validation: Valid", error.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(answer, error.ToString(), StringComparison.Ordinal);
                Assert.Contains(answer, output.ToString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(output.ToString());
                Assert.Contains("result_rejected", error.ToString(), StringComparison.Ordinal);
            }
            return;
        }
        using var envelope = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedExit == 0, envelope.RootElement.GetProperty("ok").GetBoolean());
        if (expectedExit == 0)
        {
            var data = envelope.RootElement.GetProperty("data");
            Assert.Equal(answer, data.GetProperty("answer").GetString());
            Assert.Equal("Valid", data.GetProperty("validation").GetProperty("status").GetString());
        }
        else
        {
            Assert.False(envelope.RootElement.TryGetProperty("data", out _));
            Assert.Equal("result_rejected", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("Extraction")]
    [InlineData("Classification")]
    public void Cli_contract_is_opt_in_and_requires_a_structured_profile(string profile)
    {
        Assert.True(AskCommand.TryParse(["extract", "--profile", profile, "--schema", "contract.json"], out var request, out _));
        Assert.Equal("contract.json", request!.SchemaPath);
        Assert.False(AskCommand.TryParse(["extract", "--schema", "contract.json"], out _, out var refusal));
        Assert.Equal("schema_profile_not_supported", refusal!.Code);
        Assert.False(AskCommand.TryParse(["extract", "--profile", profile, "--schema", "a", "--schema", "b"], out _, out refusal));
        Assert.Equal("schema_invalid_argument", refusal!.Code);
        Assert.True(AskCommand.TryParse(["summarize"], out request, out _));
        Assert.Null(request!.SchemaPath);
    }

    [Fact]
    public async Task Cli_schema_reader_is_bounded_and_cancellable()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(Contract()), TestContext.Current.CancellationToken);
            Assert.Equal(3, (await AskCommand.ReadSchemaAsync(file, TestContext.Current.CancellationToken))!.Fields.Count);
            await File.WriteAllTextAsync(file, new string(' ', StructuredOutputSchema.MaximumSchemaCharacters + 2), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => AskCommand.ReadSchemaAsync(file, TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskCommand.ReadSchemaAsync(file, cancelled.Token));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Failed_cli_output_has_typed_error_no_usable_value_and_legacy_refusals_are_unchanged()
    {
        using var rejected = JsonDocument.Parse(MachineEnvelope.StructuredRefusal("ask", StructuredOutputValidator.Validate("{}", Contract(), TestContext.Current.CancellationToken)));
        Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(rejected.RootElement.TryGetProperty("data", out _));
        var error = rejected.RootElement.GetProperty("error");
        Assert.Equal("result_rejected", error.GetProperty("code").GetString());
        Assert.Equal("SchemaMismatch", error.GetProperty("validation").GetProperty("status").GetString());
        using var legacy = JsonDocument.Parse(MachineEnvelope.Refusal("ask", "input_rejected", "bad input"));
        Assert.False(legacy.RootElement.GetProperty("error").TryGetProperty("validation", out _));
    }

    [Fact]
    public void Schema_survives_broker_round_trip_and_model_resolution_while_legacy_payload_omits_it()
    {
        var request = LocalJobRequestFactory.CreateRoutedChat("extract", LocalJobPriority.Foreground, LocalTaskProfile.Extraction,
            "Extract", null, null, new LocalWorkloadMetadata(10, 10, 0, 0, 0, LocalDurationClass.Short), outputSchema: Contract());
        var json = JsonSerializer.Serialize(request, LocalAiJson.Strict);
        var restored = JsonSerializer.Deserialize<LocalJobRequest>(json, LocalAiJson.Strict)!;
        var resolved = LocalJobRequestFactory.ResolveRoutedChat(restored, "model");
        Assert.Equal(JsonSerializer.Serialize(Contract()), JsonSerializer.Serialize(Assert.IsType<ChatJobPayload>(resolved.Payload).OutputSchema));
        var legacy = new ChatJobPayload("model", "prompt", null, null);
        Assert.DoesNotContain("outputSchema", JsonSerializer.Serialize(legacy, LocalAiJson.Strict), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ChatJobPayload("model", "prompt", null, null, outputSchema: Contract()));
    }

    private static StructuredOutputSchema Contract() => StructuredOutputSchema.Parse("""
        {"schemaVersion":1,"fields":[
          {"name":"label","type":"String","allowedValues":["bug","question"]},
          {"name":"count","type":"Integer"},
          {"name":"verified","type":"Boolean"}
        ]}
        """);

    [Theory]
    [InlineData("", StructuredValidationStatus.Empty)]
    [InlineData("   ", StructuredValidationStatus.Empty)]
    [InlineData("Here is the result: {\"label\":\"bug\",\"count\":1,\"verified\":true}", StructuredValidationStatus.InvalidJson)]
    [InlineData("{\"label\":\"bug\",\"count\":\"1\",\"verified\":true}", StructuredValidationStatus.SchemaMismatch)]
    [InlineData("{\"label\":\"bug\",\"count\":1}", StructuredValidationStatus.SchemaMismatch)]
    public void A_nominal_model_success_does_not_validate_wrong_or_incomplete_output(string answer, StructuredValidationStatus expected)
    {
        var result = StructuredOutputValidator.Validate(answer, Contract(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.False(result.IsValid);
        Assert.Null(result.Value);
    }

    [Fact]
    public void A_valid_object_returns_owned_data_and_no_validation_errors()
    {
        var result = StructuredOutputValidator.Validate("""{"label":"bug","count":3,"verified":true}""",
            Contract(), TestContext.Current.CancellationToken);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("bug", result.Value!.Value.GetProperty("label").GetString());
        Assert.Equal(3, result.Value.Value.GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("{\"label\":\"other\",\"count\":1,\"verified\":true}", "value_not_allowed", "/label")]
    [InlineData("{\"label\":\"bug\",\"count\":1,\"verified\":true,\"extra\":5}", "unexpected_field", "/extra")]
    [InlineData("{\"label\":\"bug\",\"label\":\"question\",\"count\":1,\"verified\":true}", "duplicate_field", "/label")]
    [InlineData("{\"label\":\"bug\",\"count\":1.5,\"verified\":true}", "wrong_type", "/count")]
    [InlineData("{\"label\":\"bug\",\"count\":9223372036854775808,\"verified\":true}", "wrong_type", "/count")]
    [InlineData("{\"label\":\"bug\",\"count\":1,\"verified\":null}", "wrong_type", "/verified")]
    [InlineData("{}", "required_field_missing", "/label")]
    [InlineData("[]", "object_required", "")]
    public void Errors_identify_the_constraint_and_field(string answer, string code, string path)
    {
        var result = StructuredOutputValidator.Validate(answer, Contract(), TestContext.Current.CancellationToken);
        Assert.Equal(StructuredValidationStatus.SchemaMismatch, result.Status);
        Assert.Contains(new StructuredValidationError(code, path), result.Errors);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("```json\n{}\n```")]
    [InlineData("{} trailing")]
    [InlineData("{} {}")]
    [InlineData("{\"label\":")]
    public void Code_fences_truncation_and_extra_text_are_not_silently_repaired(string answer)
    {
        Assert.Equal(StructuredValidationStatus.InvalidJson,
            StructuredOutputValidator.Validate(answer, Contract(), TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void Missing_contract_is_not_a_validation_pass()
    {
        var result = StructuredOutputValidator.Validate("anything", null, TestContext.Current.CancellationToken);
        Assert.Equal(StructuredValidationStatus.NotRequested, result.Status);
        Assert.False(result.IsValid);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Optional_fields_and_json_pointer_escaping_are_explicit()
    {
        var schema = new StructuredOutputSchema(1, [new("a/b~c", StructuredFieldType.Number, Required: false)]);
        Assert.True(StructuredOutputValidator.Validate("{}", schema, TestContext.Current.CancellationToken).IsValid);
        Assert.True(StructuredOutputValidator.Validate("""{"a/b~c":1.25}""", schema, TestContext.Current.CancellationToken).IsValid);
        var invalid = StructuredOutputValidator.Validate("""{"a/b~c":"1.25"}""", schema, TestContext.Current.CancellationToken);
        Assert.Equal("/a~1b~0c", Assert.Single(invalid.Errors).Path);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"fields\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"fields\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[{\"name\":\"a\",\"type\":0}]}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[{\"name\":\"a\",\"type\":\"0\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[{\"name\":\"a\",\"type\":\"String\"},{\"name\":\"a\",\"type\":\"String\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[{\"name\":\"a\",\"type\":\"Integer\",\"allowedValues\":[\"x\"]}]}")]
    public void Ambiguous_or_unsupported_contracts_are_rejected(string schema)
    {
        Assert.ThrowsAny<ArgumentException>(() => StructuredOutputSchema.Parse(schema));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"fields\":[{\"name\":\"a\",\"type\":\"String\",\"unknown\":true}]}")]
    [InlineData("{\"schemaVersion\":1,\"fields\":[{\"name\":\"a\"}]}")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"type\":\"String\"}]}")]
    public void Unknown_schema_keywords_and_required_metadata_do_not_default_to_success(string schema)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => StructuredOutputSchema.Parse(schema));
    }

    [Fact]
    public void Size_and_cancellation_are_bounded_before_parsing()
    {
        var tooLarge = new string('x', StructuredOutputValidator.MaximumResponseCharacters + 1);
        Assert.Equal("response_too_large", Assert.Single(StructuredOutputValidator.Validate(tooLarge, Contract(),
            TestContext.Current.CancellationToken).Errors).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StructuredOutputValidator.Validate(tooLarge, Contract(), cancellation.Token));
    }
}
