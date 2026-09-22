using System.Buffers.Binary;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Broker.Client;
using LocalAi.Tests.Shared;
using LocalLm.Core;
using LocalAi.Mcp;
using ModelContextProtocol.Protocol;

namespace LocalLm.Tests;

public sealed class LocalTasksTests
{
    [Fact]
    public async Task Interrupted_translation_reuses_the_validated_segment_and_preserves_protected_bytes()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        const string source = "# Before\r\n\r\n```cs\r\nvar x = 1;\r\n```\r\n\r\nAfter";
        using var cancellation = new CancellationTokenSource();
        try
        {
            var first = new FakeLocalModelClient
            {
                RoutedAnswerFactory = (_, _) => { cancellation.Cancel(); return "# До"; },
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalTasks(first).TranslateAsync(
                source, "English", "Russian", true, TranslationDelivery.ToCaller, cancellation.Token, checkpoint));
            var next = new FakeLocalModelClient
            {
                RoutedAnswerFactory = (call, _) => call.Prompt.EndsWith("After", StringComparison.Ordinal) ? "После" : "# Повтор",
            };
            var result = await new LocalTasks(next).TranslateAsync(source, "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            Assert.Single(next.Calls);
            Assert.StartsWith("# До\r\n\r\n```cs\r\nvar x = 1;\r\n```\r\n\r\nПосле", result.Answer, StringComparison.Ordinal);
            Assert.DoesNotContain("var x", next.Calls[0].Prompt, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(checkpoint)) Directory.Delete(checkpoint, recursive: true); }
    }

    [Fact]
    public async Task Complete_translation_checkpoint_returns_unicode_without_replaying_usage()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await new LocalTasks(new FakeLocalModelClient
            {
                Answer = "Привет 😀", QueueDuration = TimeSpan.FromSeconds(7), ExecutionDuration = TimeSpan.FromSeconds(11),
            }).TranslateAsync("Hello there", "English", "Russian", false, TranslationDelivery.ToCaller,
                TestContext.Current.CancellationToken, checkpoint);
            using var usage = new UsageCall(UsageSurface.Mcp, "locallm", "translate_local");
            var next = new FakeLocalModelClient();
            var result = await new LocalTasks(next).TranslateAsync("Hello there", "English", "Russian", false,
                TranslationDelivery.ToFile, TestContext.Current.CancellationToken, checkpoint);
            Assert.Equal(first.Answer, result.Answer);
            Assert.Empty(next.Calls);
            Assert.Empty(next.ExperimentCompletions);
            Assert.Equal(0, result.LocalTokensProcessed);
            Assert.Null(result.Model);
            Assert.Null(result.Receipt);
            Assert.False(result.NoTranslatableText);
            Assert.Contains("checkpoint", result.Notice, StringComparison.OrdinalIgnoreCase);
            var report = usage.Complete();
            Assert.Equal(UsageCache.Hit, report.Models.Cache);
            Assert.Equal(UsageModelState.NotCalled, report.Models.State);
            Assert.Empty(report.Models.Actual);
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Fact]
    public async Task Long_plain_translation_resumes_chunk_order_after_a_late_cancellation()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        var source = string.Concat(Enumerable.Repeat("First paragraph. ", 3000)) + "Final paragraph.";
        using var cancellation = new CancellationTokenSource();
        try
        {
            var first = new FakeLocalModelClient
            {
                RoutedAnswerFactory = (_, _) => { cancellation.Cancel(); return "Первый фрагмент. "; },
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalTasks(first).TranslateAsync(source,
                "English", "Russian", false, TranslationDelivery.ToCaller, cancellation.Token, checkpoint));
            var next = new FakeLocalModelClient { Answer = "Последний фрагмент." };
            var result = await new LocalTasks(next).TranslateAsync(source, "English", "Russian", false,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            Assert.EndsWith("Final paragraph.", Assert.Single(next.Calls).Prompt, StringComparison.Ordinal);
            Assert.StartsWith("Первый фрагмент. Последний фрагмент.", result.Answer, StringComparison.Ordinal);
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("sourceLanguage")]
    [InlineData("targetLanguage")]
    [InlineData("markdown")]
    [InlineData("route")]
    [InlineData("processor")]
    [InlineData("language")]
    public async Task Translation_checkpoint_rejects_changed_identity_before_inference(string changed)
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new LocalTasks(new FakeLocalModelClient { Answer = "Привет" }).TranslateAsync("Hello", "English", "Russian", false,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            if (changed is "route" or "processor" or "language")
            {
                var path = Path.Combine(checkpoint, "session.json");
                var manifest = JsonSerializer.Deserialize<TranslationCheckpoint.Manifest>(await File.ReadAllTextAsync(path,
                    TestContext.Current.CancellationToken), LocalAiJson.Strict)!;
                manifest = changed switch
                {
                    "route" => manifest with { RouteVersion = "changed-route" },
                    "processor" => manifest with { ProcessorVersion = "changed-processor" },
                    _ => manifest with { Language = "changed-language" },
                };
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, LocalAiJson.Strict), TestContext.Current.CancellationToken);
            }
            var next = new FakeLocalModelClient();
            await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(next).TranslateAsync(
                changed == "source" ? "Different" : "Hello", changed == "sourceLanguage" ? "French" : "English",
                changed == "targetLanguage" ? "German" : "Russian", changed == "markdown", TranslationDelivery.ToCaller,
                TestContext.Current.CancellationToken, checkpoint));
            Assert.Empty(next.Calls);
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Fact]
    public async Task Translation_fallback_reuses_validated_segments_and_counts_only_new_calls()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        const string source = "# Before\n\n```cs\nvar x = 1;\n```\n\nAfter {name}";
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b"]);
        try
        {
            var client = new FakeLocalModelClient { QueueDuration = TimeSpan.FromSeconds(2), ExecutionDuration = TimeSpan.FromSeconds(3) };
            client.Answers.Enqueue("# До");
            client.Answers.Enqueue("После"); // Invalid: missing protected placeholder.
            client.Answers.Enqueue("После {name}");
            var result = await catalog.Tasks(client).TranslateAsync(source, "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            Assert.Equal(3, client.Calls.Count);
            Assert.EndsWith("After {name}", client.Calls[2].Prompt, StringComparison.Ordinal);
            Assert.Equal("qwen3.5:9b", client.Calls[2].ModelOverride);
            Assert.StartsWith("# До\n\n```cs\nvar x = 1;\n```\n\nПосле {name}", result.Answer, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromSeconds(6), result.Receipt!.QueueDuration);
            Assert.Equal(TimeSpan.FromSeconds(9), result.Receipt.ExecutionDuration);
            Assert.Equal(client.Calls.Sum(call => TokenEstimator.ForText(call.Prompt)) +
                new[] { "# До", "После", "После {name}" }.Sum(TokenEstimator.ForText), result.LocalTokensProcessed);
            Assert.Equal(ModelExecutionOutcome.StructuralFailure, Assert.Single(client.ExperimentCompletions).Outcome);
            var next = new FakeLocalModelClient();
            var cached = await catalog.Tasks(next).TranslateAsync(source, "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            Assert.Equal(result.Answer, cached.Answer);
            Assert.Empty(next.Calls);
            Assert.Empty(next.ExperimentCompletions);
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_translation_checkpoint_is_refused_even_with_a_recomputed_text_hash(bool rehash)
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new LocalTasks(new FakeLocalModelClient { Answer = "Привет {name}" }).TranslateAsync("Hello {name}", "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            var path = Path.Combine(checkpoint, "segment-0.json");
            var segment = JsonSerializer.Deserialize<TranslationCheckpoint.Segment>(await File.ReadAllTextAsync(path,
                TestContext.Current.CancellationToken), LocalAiJson.Strict)!;
            segment = segment with { Text = "Изменено", TextHash = rehash ? TranslationCheckpoint.Hash("Изменено") : segment.TextHash };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(segment, LocalAiJson.Strict), TestContext.Current.CancellationToken);
            var next = new FakeLocalModelClient();
            await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(next).TranslateAsync("Hello {name}", "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint));
            Assert.Empty(next.Calls);
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Fact]
    public async Task Missing_translation_segment_is_recomputed_without_replaying_other_segments()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        const string source = "Before\n\n```cs\ncode\n```\n\nAfter";
        try
        {
            var first = new FakeLocalModelClient();
            first.Answers.Enqueue("До"); first.Answers.Enqueue("После");
            await new LocalTasks(first).TranslateAsync(source, "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            File.Delete(Path.Combine(checkpoint, "segment-1.json"));
            var next = new FakeLocalModelClient { Answer = "После", QueueDuration = TimeSpan.FromSeconds(2) };
            var result = await new LocalTasks(next).TranslateAsync(source, "English", "Russian", true,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            Assert.EndsWith("After", Assert.Single(next.Calls).Prompt, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromSeconds(2), result.Receipt!.QueueDuration);
            Assert.Equal(TokenEstimator.ForText(next.Calls[0].Prompt) + TokenEstimator.ForText("После"), result.LocalTokensProcessed);
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Fact]
    public async Task Translation_checkpoint_has_one_writer_and_releases_its_lease()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), "localai-translation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new LocalTasks(new FakeLocalModelClient { Answer = "Привет" }).TranslateAsync("Hello", "English", "Russian", false,
                TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint);
            using (var lease = new FileStream(Path.Combine(checkpoint, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var next = new FakeLocalModelClient();
                await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(next).TranslateAsync("Hello", "English", "Russian", false,
                    TranslationDelivery.ToCaller, TestContext.Current.CancellationToken, checkpoint));
                Assert.Empty(next.Calls);
            }
        }
        finally { Directory.Delete(checkpoint, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_translation_comparison_requires_actual_delivery(bool failDelivery)
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-file-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.txt");
            var targetPath = Path.Combine(root, "translation.txt");
            await File.WriteAllTextAsync(sourcePath, "Hello.", TestContext.Current.CancellationToken);
            var tasks = new LocalTasks(new FakeLocalModelClient
            {
                RoutedAnswerFactory = (_, _) =>
                {
                    // The destination becomes invalid after preflight, during generation.
                    if (failDelivery) Directory.CreateDirectory(targetPath);
                    return "Привет.";
                },
            });
            var response = await new UsageReporting(root).InvokeAsync("locallm", "translate_local", Guid.NewGuid(), async token =>
                new CallToolResult { Content = [new TextContentBlock { Text = await LocalLm.Mcp.LocalLmTools.TranslateLocal(tasks,
                    "English", "Russian", path: sourcePath, outPath: targetPath, cancellationToken: token) }] },
                TestContext.Current.CancellationToken, requestJson: "{\"path\":\"source.txt\",\"outPath\":\"translation.txt\"}");
            var report = UsageReportJson.Read(response.Meta!["localai/usage"]!.ToJsonString());
            Assert.Equal(O200kCloudTokenProfile.Instance.CountText("Hello."), report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
            Assert.DoesNotContain("Привет.", string.Join("\n", response.Content.OfType<TextContentBlock>().Select(block => block.Text)), StringComparison.Ordinal);
            if (failDelivery)
            {
                Assert.Equal(UsageDelivery.Failed, report.Outcome.Delivery);
                Assert.Equal(UsageValue.Unknown("result_not_delivered"), report.CloudTokens[UsageCloudTokens.InputSaved]);
                Assert.Equal(UsageValue.Unknown("result_not_delivered"), report.CloudTokens[UsageCloudTokens.OutputSaved]);
            }
            else
            {
                Assert.Equal(UsageDelivery.Delivered, report.Outcome.Delivery);
                Assert.Equal(O200kCloudTokenProfile.Instance.CountText(await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken)),
                    report.CloudTokens[UsageCloudTokens.OutputWithoutDelegation]);
                Assert.Equal(UsageValueStatus.Estimated, report.CloudTokens[UsageCloudTokens.OutputSaved].Status);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cli_counts_the_delivered_unicode_streams_arguments_and_notice()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-cli-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source.txt");
            var source = string.Concat(Enumerable.Repeat("Исходный документ с диагностическими фактами.\n", 1000));
            await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);
            using var output = new StringWriter();
            using var error = new StringWriter();
            string[] arguments = ["ask", "Резюме", path];
            var tasks = new LocalTasks(new FakeLocalModelClient { Answer = "Краткий результат." });
            var exit = await CliUsageReporting.RunAsync("localai", arguments, root, output, error, async (stdout, stderr) =>
            {
                UsageCall.Current!.Comparison.SourceContext(CloudContextPresence.NotPresent);
                var result = await tasks.AskAsync(LocalTaskProfile.ShortSummary, "Резюме", [path], null, TestContext.Current.CancellationToken);
                await stdout.WriteAsync(result.Answer);
                await stderr.WriteAsync("Готово.\n");
                return 0;
            });
            Assert.Equal(0, exit);
            var reportPath = Assert.Single(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            var report = UsageReportJson.Read(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            var profile = O200kCloudTokenProfile.Instance;
            Assert.Equal(profile.CountText(source), report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
            Assert.Equal(profile.CountText(JsonSerializer.Serialize(arguments)).Value + profile.CountText(output.ToString() + error).Value,
                report.CloudTokens[UsageCloudTokens.InputWithDelegation].Value);
            Assert.Equal(UsageValueStatus.Estimated, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
            Assert.True(report.CloudTokens[UsageCloudTokens.InputSaved].Value > 0);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(output.ToString() + error), report.Volumes[UsageVolume.ResponseBytes].Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Code_only_translation_has_no_applicable_cloud_generation_comparison()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "translate_local");
        var tasks = new LocalTasks(new FakeLocalModelClient());
        var result = await tasks.TranslateAsync("```cs\nreturn 42;\n```", "English", "Russian", true,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);
        var report = CloudTokenMethodology.Apply(call.Complete(), call.Comparison.Scenario("request"), result.Answer, call.Comparison.Profile);
        Assert.All(report.CloudTokens.Values, value => Assert.Equal(UsageValue.NotApplicable("no_translatable_text"), value));
    }

    [Theory]
    [InlineData(CloudContextPresence.NotPresent)]
    [InlineData(CloudContextPresence.AlreadyPresent)]
    [InlineData(CloudContextPresence.Unknown)]
    public async Task Real_ask_surface_reports_a_numeric_comparison_for_a_known_file(CloudContextPresence presence)
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-runtime-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(path, string.Concat(Enumerable.Repeat("A substantial source document with diagnostic facts.\n", 1000)), TestContext.Current.CancellationToken);
            var tasks = new LocalTasks(new FakeLocalModelClient { Answer = "One compact result." });
            var response = await new UsageReporting(root).InvokeAsync("locallm", "ask_local", Guid.NewGuid(), async token =>
                new CallToolResult { Content = [new TextContentBlock { Text = await LocalLm.Mcp.LocalLmTools.AskLocal(tasks, "Summarize", [path], cancellationToken: token, sourceContext: presence) }] },
                TestContext.Current.CancellationToken, requestJson: "{\"name\":\"ask_local\",\"arguments\":{\"prompt\":\"Summarize\",\"files\":[\"source.txt\"]}}");
            var report = UsageReportJson.Read(response.Meta!["localai/usage"]!.ToJsonString());
            if (presence == CloudContextPresence.Unknown)
                Assert.Equal(UsageValue.Unknown("source_context_unknown"), report.CloudTokens[UsageCloudTokens.InputSaved]);
            else
            {
                Assert.Equal(UsageValueStatus.Estimated, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
                Assert.Equal(presence == CloudContextPresence.NotPresent, report.CloudTokens[UsageCloudTokens.InputSaved].Value > 0);
            }
            Assert.NotNull(report.Comparison.ScenarioId);
            Assert.NotNull(report.Comparison.TokenizerProfile);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true, TranslationDelivery.ToCaller)]
    [InlineData(false, TranslationDelivery.ToCaller)]
    [InlineData(true, TranslationDelivery.ToFile)]
    [InlineData(false, TranslationDelivery.ToFile)]
    public async Task Translation_validates_each_fallback_and_accounts_for_every_completed_call(
        bool markdown, TranslationDelivery delivery)
    {
        using var catalog = new TranslationCatalog(markdown, ["qwen2.5-coder:14b", "qwen3.5:9b"]);
        var invalid = markdown ? "Привет" : "Translate the following fragment from English to Russian.";
        const string valid = "Привет {name}.";
        var client = new FakeLocalModelClient
        {
            QueueDuration = TimeSpan.FromSeconds(2),
            ExecutionDuration = TimeSpan.FromSeconds(3),
        };
        client.Answers.Enqueue(invalid);
        client.Answers.Enqueue(invalid);
        client.Answers.Enqueue(valid);
        var seen = new RecordingRunObserver();

        var result = await catalog.Tasks(client, seen).TranslateAsync(
            "Hello {name}.", "English", "Russian", markdown, delivery,
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "qwen2.5-coder:14b", "qwen3.5:9b" },
            client.Calls.Select(call => call.ModelOverride));
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.True(result.Validation.Passed);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Receipt!.QueueDuration);
        Assert.Equal(TimeSpan.FromSeconds(9), result.Receipt.ExecutionDuration);
        var expectedInput = client.Calls.Sum(call => TokenEstimator.ForText(call.Prompt));
        var expectedOutput = 2 * TokenEstimator.ForText(invalid) + TokenEstimator.ForText(valid);
        Assert.Equal(expectedInput + expectedOutput, result.LocalTokensProcessed);
        var completion = Assert.Single(client.ExperimentCompletions);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, completion.Outcome);
        Assert.Equal(expectedInput, completion.Metrics.InputTokens);
        Assert.Equal(expectedOutput, completion.Metrics.OutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(15), completion.Metrics.TotalDuration);
        Assert.True(completion.Metrics.UsedFallback);
        Assert.Equal(2, seen.Steps.OfType<TranslationRetrying>().Count());

        var single = await catalog.Tasks(new FakeLocalModelClient { Answer = valid }).TranslateAsync(
            "Hello {name}.", "English", "Russian", markdown, delivery,
            TestContext.Current.CancellationToken);
        Assert.Equal(single.SavedTokens, result.SavedTokens);
        Assert.Equal(single.NetCloudContextTokensSaved, result.NetCloudContextTokensSaved);
    }

    [Fact]
    public async Task Translation_exhausts_invalid_fallbacks_without_returning_their_text()
    {
        using var catalog = new TranslationCatalog(true, ["qwen2.5-coder:14b", "qwen3.5:9b"]);
        var client = new FakeLocalModelClient { Answer = "Привет" };

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken));

        Assert.Equal(3, client.Calls.Count);
        Assert.Equal(ModelExecutionOutcome.StructuralFailure, Assert.Single(client.ExperimentCompletions).Outcome);
    }

    [Fact]
    public async Task Translation_skips_duplicate_and_already_used_models()
    {
        using var catalog = new TranslationCatalog(true,
            ["translategemma:12b", "qwen2.5-coder:14b", "qwen2.5-coder:14b", "qwen3.5:9b"]);
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("Привет");
        client.Answers.Enqueue("Привет");
        client.Answers.Enqueue("Привет {name}.");

        var result = await catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "qwen2.5-coder:14b", "qwen3.5:9b" },
            client.Calls.Select(call => call.ModelOverride));
        Assert.Equal("qwen3.5:9b", result.Model);
    }

    [Fact]
    public async Task Translation_does_not_repeat_a_fallback_already_selected_by_the_broker()
    {
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b", "qwen2.5-coder:14b"]);
        var client = new FakeLocalModelClient { BrokerFailureOutcome = ModelExecutionOutcome.TechnicalFailure };
        client.Answers.Enqueue("Привет");
        client.Answers.Enqueue("Привет {name}.");

        var result = await catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "qwen2.5-coder:14b" }, client.Calls.Select(call => call.ModelOverride));
        Assert.Equal("qwen2.5-coder:14b", result.Model);
        Assert.Equal(ModelExecutionOutcome.TechnicalFailure, Assert.Single(client.ExperimentCompletions).Outcome);
    }

    [Fact]
    public async Task Translation_stops_when_all_fallback_entries_name_the_failed_primary_model()
    {
        using var catalog = new TranslationCatalog(true, ["translategemma:12b", "translategemma:12b"]);
        var client = new FakeLocalModelClient { Answer = "Привет" };

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken));

        Assert.Single(client.Calls);
        Assert.False(Assert.Single(client.ExperimentCompletions).Metrics.UsedFallback);
    }

    [Fact]
    public async Task An_empty_model_fallback_route_is_rejected_before_translation()
    {
        using var catalog = new TranslationCatalog(true, []);
        var client = new FakeLocalModelClient();

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken));

        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Translation_observes_cancellation_between_attempts(int cancelAfterCall, bool technicalFailure)
    {
        using var cancellation = new CancellationTokenSource();
        using var catalog = new TranslationCatalog(true, ["qwen2.5-coder:14b", "qwen3.5:9b"]);
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (_, index) =>
            {
                if (index == cancelAfterCall)
                {
                    cancellation.Cancel();
                    if (technicalFailure) throw new BrokerJobFailedException(Guid.NewGuid(), "ModelPreflightException");
                }
                return "Привет";
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller, cancellation.Token));

        Assert.Equal(cancelAfterCall, client.Calls.Count);
        Assert.All(client.CallCancellationTokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Empty(client.ExperimentCompletions);
    }

    [Fact]
    public async Task Cancellation_from_retry_progress_prevents_the_next_model_call()
    {
        using var cancellation = new CancellationTokenSource();
        using var catalog = new TranslationCatalog(true, ["qwen2.5-coder:14b", "qwen3.5:9b"]);
        var client = new FakeLocalModelClient { Answer = "Привет" };
        var seen = new RecordingRunObserver
        {
            OnReport = step => { if (step is TranslationRetrying) cancellation.Cancel(); },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.Tasks(client, seen).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller, cancellation.Token));

        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task Completed_fragments_of_a_failed_fallback_remain_in_the_total()
    {
        using var catalog = new TranslationCatalog(true, ["qwen2.5-coder:14b", "qwen3.5:9b"]);
        const string source = "Hello {name}.\n\n```\ncode\n```\n\nAfter.";
        var answers = new Dictionary<int, string>
        {
            [1] = "Привет", [2] = "После.", [3] = "Привет {name}.",
            [5] = "Привет {name}.", [6] = "После.",
        };
        var client = new FakeLocalModelClient
        {
            QueueDuration = TimeSpan.FromSeconds(2),
            ExecutionDuration = TimeSpan.FromSeconds(3),
            RoutedAnswerFactory = (_, index) => answers.TryGetValue(index, out var answer)
                ? answer : throw new BrokerJobFailedException(Guid.NewGuid(), "ModelPreflightException"),
        };

        var result = await catalog.Tasks(client).TranslateAsync(
            source, "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(6, client.Calls.Count);
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Receipt!.QueueDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), result.Receipt.ExecutionDuration);
        var completedInput = answers.Keys.Sum(index => TokenEstimator.ForText(client.Calls[index - 1].Prompt));
        var completedOutput = answers.Values.Sum(TokenEstimator.ForText);
        Assert.Equal(completedInput + completedOutput, result.LocalTokensProcessed);
        Assert.Equal(TimeSpan.FromSeconds(25), Assert.Single(client.ExperimentCompletions).Metrics.TotalDuration);
    }

    [Theory]
    [InlineData(false, "en", "Use the same model catalog for the client and broker")]
    [InlineData(true, "en", "Use the same model catalog for the client and broker")]
    [InlineData(false, "ru", "Используйте один и тот же каталог моделей для клиента и брокера")]
    [InlineData(true, "ru", "Используйте один и тот же каталог моделей для клиента и брокера")]
    public async Task A_catalog_mismatch_stops_translation_without_trying_another_model(
        bool duringFallback, string language, string recovery)
    {
        using var reading = TestCulture.Reading(language);
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (_, index) =>
                duringFallback && index == 1 ? "Привет" : throw new BrokerJobFailedException(
                    Guid.NewGuid(), "ModelCatalogMismatchException",
                    "Local model catalog mismatch: client 'new', broker 'old'."),
        };

        var error = await Assert.ThrowsAsync<BrokerJobFailedException>(() => new LocalTasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken));

        Assert.Equal(duringFallback ? 2 : 1, client.Calls.Count);
        Assert.Equal("ModelCatalogMismatchException", error.FailureCode);
        Assert.Contains(recovery, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "qwen3.5:9b")]
    [InlineData(false, "qwen2.5-coder:14b")]
    public async Task Translation_uses_only_the_fallback_allowed_by_the_user_catalog(bool markdown, string fallback)
    {
        using var catalog = new TranslationCatalog(markdown, [fallback]);
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue(markdown ? "Привет" : "Translate the following fragment from English to Russian.");
        client.Answers.Enqueue("Привет {name}.");

        var result = await catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", markdown, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, fallback }, client.Calls.Select(call => call.ModelOverride));
        Assert.Equal(fallback, result.Model);
        Assert.True(result.Validation.Passed);
    }

    [Fact]
    public async Task Translation_preserves_the_custom_fallback_order_when_a_model_is_unavailable()
    {
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b", "qwen2.5-coder:14b"]);
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("Привет");
        client.Answers.Enqueue("Привет {name}.");
        client.UnavailableOverrides.Add("qwen3.5:9b");

        await catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "qwen3.5:9b", "qwen2.5-coder:14b" },
            client.Calls.Select(call => call.ModelOverride));
    }

    [Fact]
    public async Task Translation_keeps_its_catalog_snapshot_when_the_file_changes_during_inference()
    {
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b"]);
        var expectedVersion = catalog.Store.Read().CatalogVersion;
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (_, index) =>
            {
                if (index == 1)
                {
                    catalog.Write(["qwen2.5-coder:14b"]);
                    return "Привет";
                }
                return "Привет {name}.";
            },
        };

        await catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal("qwen3.5:9b", client.Calls[1].ModelOverride);
        Assert.All(client.Workflows, workflow =>
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(workflow);
            Assert.True(json.TryGetProperty("CatalogVersion", out var version));
            Assert.Equal(expectedVersion, version.GetString());
        });
    }

    [Fact]
    public async Task Every_translation_call_reads_a_fresh_catalog_snapshot()
    {
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b"]);
        var client = new FakeLocalModelClient();
        var tasks = catalog.Tasks(client);
        client.Answers.Enqueue("Привет");
        client.Answers.Enqueue("Привет {name}.");
        await tasks.TranslateAsync("Hello {name}.", "English", "Russian", true,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);
        catalog.Write(["qwen2.5-coder:14b"]);
        client.Answers.Enqueue("Привет");
        client.Answers.Enqueue("Привет {name}.");
        await tasks.TranslateAsync("Hello {name}.", "English", "Russian", true,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "qwen3.5:9b", null, "qwen2.5-coder:14b" },
            client.Calls.Select(call => call.ModelOverride));
    }

    [Fact]
    public async Task The_primary_translation_request_carries_the_catalog_snapshot()
    {
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b"]);
        var client = new FakeLocalModelClient { Answer = "Привет {name}." };
        await catalog.Tasks(client).TranslateAsync("Hello {name}.", "English", "Russian", true,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonSerializer.SerializeToElement(Assert.Single(client.Workflows));
        Assert.True(json.TryGetProperty("CatalogVersion", out var version));
        Assert.Equal(catalog.Store.Read().CatalogVersion, version.GetString());
    }

    [Fact]
    public async Task Translation_does_not_escape_the_catalog_when_all_its_fallbacks_are_unavailable()
    {
        using var catalog = new TranslationCatalog(true, ["qwen3.5:9b"]);
        var client = new FakeLocalModelClient { Answer = "Привет" };
        client.UnavailableOverrides.Add("qwen3.5:9b");

        await Assert.ThrowsAsync<BrokerJobFailedException>(() => catalog.Tasks(client).TranslateAsync(
            "Hello {name}.", "English", "Russian", true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken));

        Assert.Equal(new string?[] { null, "qwen3.5:9b" }, client.Calls.Select(call => call.ModelOverride));
    }

    private sealed class TranslationCatalog : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "localai-translation-catalog-" + Guid.NewGuid().ToString("N"));
        private readonly bool markdown;
        public ModelRoutingCatalogStore Store { get; }

        public TranslationCatalog(bool markdown, string[] fallbacks)
        {
            this.markdown = markdown;
            Store = new ModelRoutingCatalogStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
            Write(fallbacks);
        }

        public void Write(string[] fallbacks)
        {
            var profile = markdown ? LocalTaskProfile.TechnicalTranslation : LocalTaskProfile.PlainTranslation;
            var document = ModelRoutingCatalogResource.LoadDocument();
            document = document with
            {
                Routes = document.Routes.Select(route => route.Profile == profile
                    ? route with { Candidates = ["translategemma:12b"], Fallbacks = fallbacks }
                    : route).ToArray(),
            };
            File.WriteAllText(Store.FilePath, System.Text.Json.JsonSerializer.Serialize(document, LocalAiJson.Strict));
        }

        public LocalTasks Tasks(FakeLocalModelClient client, ILocalRunObserver? observer = null) =>
            new(client, new LogTriagePolicyStore(root), observer, modelCatalog: Store);

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Triage_uses_the_configured_route_and_the_same_catalog_identity_as_the_broker()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-triage-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelRoutingCatalogStore(root);
            var document = ModelRoutingCatalogResource.LoadDocument();
            document = document with
            {
                Routes = document.Routes.Select(route => route.Profile == LocalTaskProfile.LogTriage
                    ? route with { Candidates = ["qwen3.5:9b"], Fallbacks = ["qwen3.5:9b"] }
                    : route).ToArray(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, System.Text.Json.JsonSerializer.Serialize(document, LocalAiJson.Strict));
            var client = new FakeLocalModelClient { CatalogVersion = store.Read().CatalogVersion };
            var tasks = new LocalTasks(client, new LogTriagePolicyStore(root), modelCatalog: store);

            await tasks.TriageLogAsync(null, "error CS1000", null, null, TestContext.Current.CancellationToken);

            Assert.Equal("qwen3.5:9b", Assert.Single(client.Calls).ModelOverride);
            Assert.NotEmpty(client.PreflightCatalogVersions);
            Assert.All(client.PreflightCatalogVersions, version => Assert.Equal(client.CatalogVersion, version));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A model answers in the language of the material it was given, so a Russian reader asking
    /// about an English build log got English back — their own machine's language never entered
    /// into it. The language the product already prints its own strings in is the one the answer
    /// should come in, and naming it in the prompt is the whole mechanism: measured on this
    /// machine, the model then answers in it and still leaves paths, line numbers and error codes
    /// exactly as they were.
    ///
    /// The instruction is English whichever reader it serves, because it is addressed to the
    /// model rather than to them.
    /// </summary>
    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("en", "English")]
    public async Task A_triage_asks_for_the_answer_in_the_readers_language(
        string reader,
        string named)
    {
        using var reading = TestCulture.Reading(reader);
        var client = new FakeLocalModelClient();
        var tasks = new LocalTasks(client);

        await tasks.TriageLogAsync(
            path: null,
            text: "fatal DIRECT_TEXT_42",
            question: null,
            model: null,
            TestContext.Current.CancellationToken);

        Assert.All(
            client.Calls,
            call => Assert.Contains(named, call.System!, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same for the ask surface, which has its own system prompt and would otherwise have
    /// been fixed in one place and forgotten in the other.
    /// </summary>
    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("en", "English")]
    public async Task An_ask_asks_for_the_answer_in_the_readers_language(
        string reader,
        string named)
    {
        using var reading = TestCulture.Reading(reader);
        var client = new FakeLocalModelClient();
        var tasks = new LocalTasks(client);

        await tasks.AskAsync(
            "list the TODOs",
            [],
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Contains(named, Assert.Single(client.Calls).System!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_triage_uses_fixed_profile_and_the_largest_full_vram_context()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "error CS1000",
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient();
            var tasks = new LocalTasks(client);

            await tasks.TriageLogAsync(
                path,
                null,
                model: null,
                TestContext.Current.CancellationToken);

            var call = Assert.Single(client.Calls);
            Assert.Equal(LocalTaskProfile.LogTriage, call.Profile);
            Assert.Equal("qwen2.5-coder:14b", call.ModelOverride);
            Assert.DoesNotContain("qwen3.6:27b", call.Prompt, StringComparison.Ordinal);
            Assert.Equal(32768, call.RequestedContextTokens);
            Assert.Equal(
                [new ModelContextRef("qwen2.5-coder:14b", 32768)],
                client.Preflights);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Log_triage_accepts_direct_text_and_requires_exactly_one_source()
    {
        var client = new FakeLocalModelClient();
        var tasks = new LocalTasks(client);

        var result = await tasks.TriageLogAsync(
            path: null,
            text: "fatal DIRECT_TEXT_42",
            question: "Find the fatal marker.",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Contains("DIRECT_TEXT_42", Assert.Single(client.Calls).Prompt);
        Assert.Contains("Log text read", result.Detail, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.TriageLogAsync(
            path: null,
            text: null,
            question: null,
            model: null,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.TriageLogAsync(
            path: "some.log",
            text: "also text",
            question: null,
            model: null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Log_triage_streams_large_file_and_reduces_fragments_sequentially()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"localai-large-log-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "FIRST_BOUNDARY\n" + new string('x', 50_000) + "\nLAST_BOUNDARY",
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient
            {
                MaximumPreflightContextTokens = 2048,
                RoutedAnswerFactory = (call, index) =>
                    call.Prompt.Contains(
                        "BEGIN LOG FRAGMENT",
                        StringComparison.Ordinal)
                        ? $"partial finding {index}"
                        : $"combined finding {index}"
            };
            var tasks = new LocalTasks(client);

            var result = await tasks.TriageLogAsync(
                path,
                "Preserve boundary markers.",
                model: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [32768, 16384, 8192, 4096, 2048],
                client.Preflights.Select(preflight => preflight.ContextTokens));
            var mapCalls = client.Calls.Where(call => call.Prompt.Contains(
                "BEGIN LOG FRAGMENT",
                StringComparison.Ordinal)).ToArray();
            var reduceCalls = client.Calls.Except(mapCalls).ToArray();
            Assert.True(mapCalls.Length > 1);
            Assert.NotEmpty(reduceCalls);
            Assert.Contains("FIRST_BOUNDARY", mapCalls.First().Prompt);
            Assert.Contains("LAST_BOUNDARY", mapCalls.Last().Prompt);
            Assert.All(client.Calls, call =>
            {
                Assert.Equal("qwen2.5-coder:14b", call.ModelOverride);
                Assert.Equal(2048, call.RequestedContextTokens);
            });
            Assert.Equal(1, client.MaximumConcurrentCalls);
            Assert.Contains("fragments:", result.Detail, StringComparison.Ordinal);
            Assert.StartsWith("combined finding", result.Answer, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Log_triage_reloads_policy_for_every_invocation()
    {
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            $"localai-log-policy-{Guid.NewGuid():N}");
        try
        {
            var store = new LogTriagePolicyStore(runtimeRoot);
            store.Write(LogTriagePolicy.Default with { MaximumContextTokens = 8192 });
            var client = new FakeLocalModelClient();
            var tasks = new LocalTasks(client, store);

            await tasks.TriageLogAsync(
                path: null,
                text: "first invocation",
                question: null,
                model: null,
                TestContext.Current.CancellationToken);
            store.Write(LogTriagePolicy.Default with { MaximumContextTokens = 4096 });
            await tasks.TriageLogAsync(
                path: null,
                text: "second invocation",
                question: null,
                model: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [8192, 4096],
                client.Preflights.Select(preflight => preflight.ContextTokens));
            Assert.Equal(
                [8192, 4096],
                client.Calls.Select(call => call.RequestedContextTokens!.Value));
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// ask_local reaches AskAsync directly, so the console parser protects a caller this one never
    /// sees. LogTriage owns LogTriagePipeline — its own prompt, its own fragmenting, its own
    /// capacity probe — and ask enters none of it; an agent handed a log through ask_local got one
    /// prompt over the whole file and no sign that the triage had not run.
    /// </summary>
    [Fact]
    public async Task Ask_refuses_a_profile_whose_work_belongs_to_another_tool()
    {
        var client = new FakeLocalModelClient();
        var tasks = new LocalTasks(client);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tasks.AskAsync(
                LocalTaskProfile.LogTriage,
                "what failed",
                [],
                model: null,
                TestContext.Current.CancellationToken));

        Assert.Contains("triage_log", exception.Message, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Ask_rejects_translation_profiles_that_bypass_validation()
    {
        var client = new FakeLocalModelClient();
        var tasks = new LocalTasks(client);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tasks.AskAsync(
                LocalTaskProfile.PlainTranslation,
                "translate",
                [],
                model: null,
                TestContext.Current.CancellationToken));

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Image_translation_completes_one_experiment_workflow()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"localai-image-translation-{Guid.NewGuid():N}.png");
        var header = new byte[32];
        header[0] = 0x89;
        header[1] = (byte)'P';
        header[2] = (byte)'N';
        header[3] = (byte)'G';
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), 100);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), 80);
        try
        {
            await File.WriteAllBytesAsync(
                path,
                header,
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient
            {
                Answer = "Переведённый текст"
            };
            var tasks = new LocalTasks(client);

            await tasks.ReadImageAsync(
                [path],
                "Translate all visible text into Russian.",
                LocalTaskProfile.ImageTranslation,
                model: null,
                together: false,
                TestContext.Current.CancellationToken);

            var call = Assert.Single(client.Calls);
            Assert.Equal(LocalTaskProfile.ImageTranslation, call.Profile);
            Assert.Contains(
                "Translate",
                call.System,
                StringComparison.Ordinal);
            Assert.NotNull(call.WorkflowId);
            var completion = Assert.Single(client.ExperimentCompletions);
            Assert.Equal(call.WorkflowId, completion.WorkflowId);
            Assert.Equal(LocalTaskProfile.ImageTranslation, completion.Profile);
            Assert.Equal(ModelExecutionOutcome.Success, completion.Outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Technical_translation_chunks_validates_and_attributes_actual_model()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "# Перевод\r\n\r\nТекст с `Code()`."
        };
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalTaskProfile.TechnicalTranslation, Assert.Single(client.Calls).Profile);
        Assert.Contains(
            "Перевод выполнен локальной моделью: translategemma:12b.",
            result.Answer,
            StringComparison.Ordinal);
        Assert.True(result.Validation.Passed);
    }

    [Fact]
    public async Task Structural_failure_retries_with_established_technical_fallback()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст без защищённого токена.");
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст с `Code()`.");
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls.Count);
        Assert.Null(client.Calls[0].ModelOverride);
        Assert.Equal("qwen2.5-coder:14b", client.Calls[1].ModelOverride);
        Assert.Equal("qwen2.5-coder:14b", result.Model);
        Assert.Contains(
            "Перевод выполнен локальной моделью: qwen2.5-coder:14b.",
            result.Answer,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The figure the notice ends on has to mean what it says. A translation that comes back is
    /// paid for twice over - the source was in the caller's context and the answer joins it - so
    /// only the difference between them was saved. One written to a file puts neither there, and
    /// the whole of both is the saving; reporting the smaller number understates exactly the
    /// route built to make it large.
    /// </summary>
    [Fact]
    public async Task A_translation_bound_for_a_file_counts_the_context_it_kept_out()
    {
        const string source = "English version";
        const string translated = "Английская версия";

        var written = await new LocalTasks(new FakeLocalModelClient { Answer = translated })
            .TranslateAsync(
                source,
                "English",
                "Russian",
                markdown: false,
                TranslationDelivery.ToFile,
                TestContext.Current.CancellationToken);

        var returned = await new LocalTasks(new FakeLocalModelClient { Answer = translated })
            .TranslateAsync(
                source,
                "English",
                "Russian",
                markdown: false,
                TranslationDelivery.ToCaller,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            TokenEstimator.ForTranslation(source, translated, TranslationDelivery.ToFile)
                .EstimatedNetCloudContextTokensSaved,
            written.NetCloudContextTokensSaved);
        Assert.True(
            written.NetCloudContextTokensSaved > returned.NetCloudContextTokensSaved,
            "A document that never reaches a context cannot save less than one that does.");
    }

    [Fact]
    public async Task Plain_translation_validation_failure_retries_with_established_fallback()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue(
            "Translate the following fragment from English to Russian.");
        client.Answers.Enqueue("Английская версия");
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "English version",
            "English",
            "Russian",
            markdown: false,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Calls.Count);
        Assert.Null(client.Calls[0].ModelOverride);
        Assert.Equal("qwen3.5:9b", client.Calls[1].ModelOverride);
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
    }

    [Fact]
    public async Task Technical_translation_tries_the_next_established_fallback()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue(
            "# Перевод\r\n\r\nТекст без защищённого токена.");
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст с `Code()`.");
        client.UnavailableOverrides.Add("qwen2.5-coder:14b");
        var tasks = new LocalTasks(client);

        var result = await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, client.Calls.Count);
        Assert.Null(client.Calls[0].ModelOverride);
        Assert.Equal("qwen2.5-coder:14b", client.Calls[1].ModelOverride);
        Assert.Equal("qwen3.5:9b", client.Calls[2].ModelOverride);
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
    }

    [Fact]
    public async Task Translation_context_accounts_for_source_and_expected_output()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "Перевод"
        };
        var tasks = new LocalTasks(client);

        await tasks.TranslateAsync(
            new string('a', 6_000),
            "English",
            "Russian",
            markdown: false,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.InRange(
            Assert.Single(client.Calls).RequestedContextTokens!.Value,
            8192,
            262144);
    }

    [Fact]
    public async Task Technical_translation_preserves_whitespace_around_fenced_code()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод");
        client.Answers.Enqueue("После блока");
        var tasks = new LocalTasks(client);
        const string source =
            "# Source\r\n\r\n" +
            "```text\r\n" +
            "value\r\n" +
            "```\r\n\r\n" +
            "After block";

        var result = await tasks.TranslateAsync(
            source,
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.StartsWith(
            "# Перевод\r\n\r\n```text\r\nvalue\r\n```\r\n\r\nПосле блока",
            result.Answer,
            StringComparison.Ordinal);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
    }

    [Theory]
    [MemberData(nameof(FencedTranslationCases.Blocks), MemberType = typeof(FencedTranslationCases))]
    public async Task Technical_translation_never_sends_fenced_source_bytes_to_the_model(
        string block, string after)
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# До");
        if (after.Length > 0) client.Answers.Enqueue("После");
        var source = "# Before\n\n" + block + after;

        var result = await new LocalTasks(client).TranslateAsync(
            source, "English", "Russian", markdown: true,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);

        Assert.Equal(after.Length > 0 ? 2 : 1, client.Calls.Count);
        Assert.All(client.Calls, call => Assert.DoesNotContain("var x", call.Prompt, StringComparison.Ordinal));
        Assert.Contains("# До\n\n" + block + after.Replace("After", "После", StringComparison.Ordinal),
            result.Answer, StringComparison.Ordinal);
        Assert.True(result.Validation.Passed, result.Validation.Detail);
        var finalValidation = TranslationValidator.ValidateMarkdown(source, result.Answer);
        Assert.True(finalValidation.Passed, finalValidation.Detail);
    }

    [Theory]
    [MemberData(nameof(FencedTranslationCases.Blocks), MemberType = typeof(FencedTranslationCases))]
    public async Task Code_only_translation_returns_the_source_without_inference(string block, string after)
    {
        var source = block + new string(after.TakeWhile(char.IsWhiteSpace).ToArray());
        foreach (var delivery in new[] { TranslationDelivery.ToCaller, TranslationDelivery.ToFile })
        {
            var client = new FakeLocalModelClient();
            var result = await new LocalTasks(client).TranslateAsync(
                source, "English", "Russian", markdown: true, delivery,
                TestContext.Current.CancellationToken);

            Assert.Equal(source, result.Answer);
            Assert.True(result.Validation.Passed, result.Validation.Detail);
            Assert.Null(result.Model);
            Assert.Null(result.Receipt);
            Assert.Equal(0, result.LocalTokensProcessed);
            Assert.Equal((int?)null, result.SavedTokens);
            Assert.Equal((int?)null, result.NetCloudContextTokensSaved);
            Assert.Empty(client.Calls);
            Assert.Empty(client.Preflights);
            Assert.Empty(client.ExperimentCompletions);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Code_blocks_separated_only_by_whitespace_need_no_translation(string newline)
    {
        var source = string.Join(newline,
            "", "```text", "привет 世界", "```", " \t", "~~~", "Translate the following fragment from", "~~~~", "");
        var client = new FakeLocalModelClient();
        var result = await new LocalTasks(client).TranslateAsync(
            source, "English", "Russian", markdown: true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(source, result.Answer);
        Assert.True(result.Validation.Passed);
        Assert.Null(result.Receipt);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("en", "No translatable text", "No model was called", "not applicable")]
    [InlineData("ru", "Нет текста для перевода", "Модель не вызывалась", "неприменимо")]
    public async Task A_deterministic_translation_notice_does_not_credit_a_model(
        string language, string reason, string noModel, string saving)
    {
        using var reading = TestCulture.Reading(language);
        var result = await new LocalTasks(new FakeLocalModelClient()).TranslateAsync(
            "```\ncode\n```", "English", "Russian", markdown: true, TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Contains(reason, result.Notice, StringComparison.Ordinal);
        Assert.Contains(noModel, result.Notice, StringComparison.Ordinal);
        Assert.Contains(saving, result.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("translategemma", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_only_translation_observes_cancellation_without_calling_a_model()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new FakeLocalModelClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalTasks(client).TranslateAsync(
            "~~~\ncode\n~~~", "English", "Russian", markdown: true, TranslationDelivery.ToCaller,
            cancellation.Token));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Only_prose_between_code_blocks_is_translated()
    {
        var client = new FakeLocalModelClient { Answer = "Перевод" };
        const string before = "```\nfirst\n```\n\n";
        const string after = "\n\n~~~\nsecond\n~~~";
        var result = await new LocalTasks(client).TranslateAsync(
            before + "Translate me" + after, "English", "Russian", markdown: true,
            TranslationDelivery.ToCaller, TestContext.Current.CancellationToken);

        var call = Assert.Single(client.Calls);
        Assert.Contains("Translate me", call.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("first", call.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("second", call.Prompt, StringComparison.Ordinal);
        Assert.Contains(before + "Перевод" + after, result.Answer, StringComparison.Ordinal);
        Assert.NotNull(result.Receipt);
        Assert.NotNull(result.Model);
    }

    [Fact]
    public async Task Chunked_translation_completes_one_experiment_workflow()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод");
        client.Answers.Enqueue("После блока");
        var tasks = new LocalTasks(client);
        const string source =
            "# Source\r\n\r\n" +
            "```text\r\nvalue\r\n```\r\n\r\n" +
            "After block";

        await tasks.TranslateAsync(
            source,
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        var completion = Assert.Single(client.ExperimentCompletions);
        Assert.Equal(
            LocalTaskProfile.TechnicalTranslation,
            completion.Profile);
        Assert.Equal("translategemma:12b", completion.Model);
        Assert.Equal(ModelExecutionOutcome.Success, completion.Outcome);
        Assert.False(completion.Metrics.UsedFallback);
        Assert.Equal(2, client.Calls.Count);
        Assert.Single(client.Calls.Select(call => call.WorkflowId).Distinct());
    }

    /// <summary>
    /// The figure the notice prints and the figure the telemetry keeps are the same claim about
    /// the same run, and they were computed by two expressions - so teaching one that a document
    /// written to a file never reaches a context left the other saying it saved nothing.
    ///
    /// Which showed up exactly where it would: `local_model_experiment_report` reported
    /// EstimatedNetCloudContextTokensSaved 0 for a pair whose runs had just reported a saving.
    /// </summary>
    [Fact]
    public async Task The_telemetry_records_the_same_saving_the_notice_prints()
    {
        const string source = "English version";
        const string translated = "\u0410\u043d\u0433\u043b\u0438\u0439\u0441\u043a\u0430\u044f "
            + "\u0432\u0435\u0440\u0441\u0438\u044f";

        var toFile = new FakeLocalModelClient { Answer = translated };
        var written = await new LocalTasks(toFile).TranslateAsync(
            source,
            "English",
            "Russian",
            markdown: false,
            TranslationDelivery.ToFile,
            TestContext.Current.CancellationToken);

        var completion = Assert.Single(toFile.ExperimentCompletions);
        Assert.Equal(
            written.NetCloudContextTokensSaved,
            completion.Metrics.EstimatedNetCloudContextTokensSaved);

        // And the text route keeps agreeing, which is the half that was already right.
        var toCaller = new FakeLocalModelClient { Answer = translated };
        var returned = await new LocalTasks(toCaller).TranslateAsync(
            source,
            "English",
            "Russian",
            markdown: false,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            returned.NetCloudContextTokensSaved,
            Assert.Single(toCaller.ExperimentCompletions)
                .Metrics.EstimatedNetCloudContextTokensSaved);
    }

    [Fact]
    public async Task Broker_fallback_completes_workflow_as_one_technical_failure()
    {
        var client = new FakeLocalModelClient
        {
            Answer = "Перевод",
            BrokerFailureOutcome = ModelExecutionOutcome.TechnicalFailure
        };
        var tasks = new LocalTasks(client);

        await tasks.TranslateAsync(
            "Translation",
            "English",
            "Russian",
            markdown: false,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        var completion = Assert.Single(client.ExperimentCompletions);
        Assert.Equal("translategemma:12b", completion.Model);
        Assert.Equal(
            ModelExecutionOutcome.TechnicalFailure,
            completion.Outcome);
        Assert.True(completion.Metrics.UsedFallback);
    }

    private sealed class FakeLocalModelClient : ILocalModelClient
    {
        public string CatalogVersion { get; init; } = "1";

        public List<string> PreflightCatalogVersions { get; } = [];

        public string Answer { get; init; } = "answer";

        public TimeSpan QueueDuration { get; init; }

        public TimeSpan ExecutionDuration { get; init; }

        public List<CancellationToken> CallCancellationTokens { get; } = [];

        public ModelExecutionOutcome? BrokerFailureOutcome { get; init; }

        public Queue<string> Answers { get; } = [];

        public int MaximumPreflightContextTokens { get; init; } = 32768;

        public Func<RoutedCall, int, string>? RoutedAnswerFactory { get; init; }

        public List<ModelContextRef> Preflights { get; } = [];

        public int MaximumConcurrentCalls { get; private set; }

        private int concurrentCalls;

        public HashSet<string> UnavailableOverrides { get; } =
            new(StringComparer.Ordinal);

        public List<RoutedCall> Calls { get; } = [];

        public List<LocalWorkflowHint?> Workflows { get; } = [];

        public List<ExperimentCompletion> ExperimentCompletions { get; } = [];

        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow,
            string? modelOverride,
            int? requestedContextTokens,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref concurrentCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            try
            {
                await Task.Yield();
                var call = new RoutedCall(
                    profile,
                    prompt,
                    system,
                    modelOverride,
                    requestedContextTokens,
                    workflow?.WorkflowId);
                Calls.Add(call);
                Workflows.Add(workflow);
                CallCancellationTokens.Add(cancellationToken);
                if (modelOverride is not null &&
                    UnavailableOverrides.Contains(modelOverride))
                {
                    throw new BrokerJobFailedException(
                        Guid.NewGuid(),
                        nameof(InvalidOperationException));
                }

                var answer = RoutedAnswerFactory?.Invoke(call, Calls.Count) ??
                             (Answers.Count == 0 ? Answer : Answers.Dequeue());
                var brokerFallback =
                    modelOverride is null &&
                    BrokerFailureOutcome is not null;
                var selectedModel = modelOverride ??
                                    (brokerFallback
                                        ? "qwen3.5:9b"
                                        : "translategemma:12b");
                return new LocalJobResult<string>(
                    answer,
                    new LocalUsageReceipt(
                        Guid.NewGuid(),
                        "local-lm",
                        "chat",
                        modelOverride ?? "translategemma:12b",
                        QueueDuration,
                        ExecutionDuration,
                        prompt.Length,
                        prompt.Length / 4,
                        null,
                        null,
                        null,
                        new LocalRoutingReceipt(
                            profile,
                            selectedModel,
                            requestedContextTokens,
                            WasCold: Calls.Count == 1,
                            UsedFallback: modelOverride is not null || brokerFallback,
                            ValidatorResult: "none:pass",
                            EstimatedGrossCloudTokensSaved: prompt.Length / 4,
                            EstimatedVerificationTokens: 0,
                            EstimatedNetCloudTokensSaved: prompt.Length / 4,
                            IsExperimentalAttempt:
                                modelOverride is null && !brokerFallback,
                            ExperimentalModel:
                                brokerFallback ? "translategemma:12b" : null,
                            ExperimentalOutcome:
                                brokerFallback ? BrokerFailureOutcome : null)));
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCalls);
            }
        }

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalJobResult<LocalModelsStatusOutput>(
                new LocalModelsStatusOutput(
                    [
                        "qwen2.5-coder:14b",
                        "qwen3.5:9b",
                        "translategemma:12b"
                    ],
                    [],
                    [],
                    [],
                    CatalogVersion,
                    [],
                    [],
                    []),
                Receipt("status", "none")));

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default)
        {
            Preflights.Add(new ModelContextRef(model, contextTokens));
            PreflightCatalogVersions.Add(catalogVersion);
            if (contextTokens > MaximumPreflightContextTokens)
            {
                throw new BrokerJobFailedException(
                    Guid.NewGuid(),
                    "ModelPreflightException");
            }

            return Task.FromResult(new LocalJobResult<LocalModelPreflightOutput>(
                new LocalModelPreflightOutput(
                    model,
                    contextTokens,
                    catalogVersion,
                    1,
                    1,
                    FullyResident: true,
                    DateTimeOffset.UtcNow),
                Receipt("preflight", model)));
        }

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentCompletionOutput>>
            CompleteExperimentAsync(
                Guid workflowId,
                LocalTaskProfile profile,
                string model,
                ModelExecutionOutcome outcome,
                LocalExperimentTaskMetrics metrics,
                CancellationToken cancellationToken = default)
        {
            ExperimentCompletions.Add(
                new ExperimentCompletion(
                    workflowId,
                    profile,
                    model,
                    outcome,
                    metrics));
            var output = new LocalExperimentCompletionOutput(
                workflowId,
                profile,
                model,
                outcome);
            return Task.FromResult(
                new LocalJobResult<LocalExperimentCompletionOutput>(
                    output,
                    new LocalUsageReceipt(
                        Guid.NewGuid(),
                        "local-lm",
                        "experiment-completion",
                        model,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        0,
                        0,
                        null,
                        null,
                        null)));
        }

        private static LocalUsageReceipt Receipt(string operation, string model) =>
            new(
                Guid.NewGuid(),
                "local-lm",
                operation,
                model,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                null,
                null,
                null);
    }

    /// <summary>
    /// A translation is one model call per fragment, and until the first line lands the console
    /// cannot say whether this run is one call or twenty. The total is known before the first
    /// call, so it is reported before the first call.
    /// </summary>
    [Fact]
    public async Task A_translation_says_which_fragment_it_is_on_before_it_starts_it()
    {
        var client = new FakeLocalModelClient { Answer = "Перевод." };
        var seen = new RecordingRunObserver();
        var tasks = new LocalTasks(client, seen);

        await tasks.TranslateAsync(
            "Text one.\r\n\r\n```csharp\r\nvar x = 1;\r\n```\r\n\r\nText two.",
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        // The fenced block is not translatable, so it is not a model call and not a fragment.
        Assert.Equal(
            [new TranslatingFragment(1, 2), new TranslatingFragment(2, 2)],
            seen.Steps.OfType<TranslatingFragment>().Take(2));
    }

    /// <summary>
    /// The fallback pass re-translates the whole document, so a run that has just doubled shows
    /// its counter restarting at 1. Without a line saying why, that reads as a defect — and until
    /// now a translation that took twice as long said so nowhere at all.
    /// </summary>
    [Fact]
    public async Task A_fallback_pass_says_the_document_is_being_translated_again()
    {
        var client = new FakeLocalModelClient();
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст без защищённого токена.");
        client.Answers.Enqueue("# Перевод\r\n\r\nТекст с `Code()`.");
        var seen = new RecordingRunObserver();
        var tasks = new LocalTasks(client, seen);

        await tasks.TranslateAsync(
            "# Source\r\n\r\nText with `Code()`.",
            "English",
            "Russian",
            markdown: true,
            TranslationDelivery.ToCaller,
            TestContext.Current.CancellationToken);

        var retry = Assert.Single(seen.Steps.OfType<TranslationRetrying>());
        Assert.Equal("qwen2.5-coder:14b", retry.Model, StringComparer.Ordinal);
        Assert.NotEmpty(retry.Detail);
    }

    /// <summary>
    /// Triage cannot say "of N" while it runs: fragments are streamed off the log and the count
    /// is not known until the log ends. So it counts what it has, and prints the total at the
    /// first moment that total is true — an invented denominator would be the one number in this
    /// output that was a guess.
    /// </summary>
    [Fact]
    public async Task A_triage_counts_its_fragments_and_names_the_total_only_at_the_end()
    {
        var client = new FakeLocalModelClient();
        var seen = new RecordingRunObserver();
        var tasks = new LocalTasks(client, seen);

        await tasks.TriageLogAsync(
            path: null,
            text: "fatal DIRECT_TEXT_42",
            question: null,
            model: null,
            TestContext.Current.CancellationToken);

        // Choosing the model comes first because it loads one: minutes of silence otherwise,
        // before anything else about the run can be said.
        Assert.IsType<TriageChoosingModel>(seen.Steps[0]);
        // Nothing consumed before the first fragment, and the whole of the text as the
        // denominator — for text piped in the unit is characters, as the record says.
        Assert.Equal(
            new TriagingFragment(1, 0, "fatal DIRECT_TEXT_42".Length),
            seen.Steps.OfType<TriagingFragment>().Single());
        Assert.Equal(1, seen.Steps.OfType<TriageLogRead>().Single().Fragments);
        Assert.True(
            seen.Steps.IndexOf(seen.Steps.OfType<TriageLogRead>().Single()) >
            seen.Steps.IndexOf(seen.Steps.OfType<TriagingFragment>().Single()),
            "the total can only be told after the fragment it counts");
    }

    private sealed class RecordingRunObserver : ILocalRunObserver
    {
        public List<LocalRunStep> Steps { get; } = [];

        public Action<LocalRunStep>? OnReport { get; init; }

        public void Report(LocalRunStep step)
        {
            Steps.Add(step);
            OnReport?.Invoke(step);
        }
    }

    private sealed record RoutedCall(
        LocalTaskProfile Profile,
        string Prompt,
        string? System,
        string? ModelOverride,
        int? RequestedContextTokens,
        Guid? WorkflowId);

    private sealed record ExperimentCompletion(
        Guid WorkflowId,
        LocalTaskProfile Profile,
        string Model,
        ModelExecutionOutcome Outcome,
        LocalExperimentTaskMetrics Metrics);
}
