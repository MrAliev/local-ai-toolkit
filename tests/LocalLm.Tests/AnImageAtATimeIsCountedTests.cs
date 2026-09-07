using System.Buffers.Binary;
using System.Text;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// Every image used to go to the model in one call, so a run over five of them could say only that
/// the model was working and for how long. A counter over that call would have lied: the images are
/// encoded in about a second and the model then works for minutes, so it would have stopped at the
/// last one and read as a hang there.
///
/// One image per call makes the counter true — each call finishes — and the partial answers are
/// merged by the same reducer log triage uses. What that costs is a question spanning the images:
/// "compare these two screenshots" cannot be answered by merging two separate readings, and the
/// reducer's own prompt forbids introducing claims absent from the parts. So the together form
/// stays, by name.
/// </summary>
public sealed class AnImageAtATimeIsCountedTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(),
        "localai-one-image-" + Guid.NewGuid().ToString("N"));

    public AnImageAtATimeIsCountedTests() => Directory.CreateDirectory(_work);

    public void Dispose() => TemporaryDirectory.Delete(_work);

    [Fact]
    public async Task Each_image_is_a_call_of_its_own()
    {
        var client = new CountingClient();

        await new LocalTasks(client).ReadImageAsync(
            Images(3),
            "transcribe the error text",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 1, 1], client.ImagesPerCall.Take(3));
    }

    /// <summary>
    /// The common case, and it must not gain a second model call: one reading is already the
    /// answer, and there is nothing to merge it with.
    /// </summary>
    [Fact]
    public async Task A_single_image_is_one_call_and_nothing_to_merge()
    {
        var client = new CountingClient();

        var result = await new LocalTasks(client).ReadImageAsync(
            Images(1),
            "transcribe the error text",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Equal([1], client.ImagesPerCall);
        Assert.Equal("reply 1", result.Answer, StringComparer.Ordinal);
    }

    /// <summary>
    /// And several end in one answer rather than a list: the merge is a call of its own, carrying
    /// the readings and no image, and what it returns is what the caller gets.
    /// </summary>
    [Fact]
    public async Task Several_images_end_in_one_merged_answer()
    {
        var client = new CountingClient();

        var result = await new LocalTasks(client).ReadImageAsync(
            Images(3),
            "transcribe the error text",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.True(
            client.ImagesPerCall.Count > 3,
            "three readings and at least one merge, so more than three calls");
        Assert.Equal(0, client.ImagesPerCall[^1]);
        Assert.Equal($"reply {client.ImagesPerCall.Count}", result.Answer, StringComparer.Ordinal);
    }

    /// <summary>
    /// The escape hatch, and the reason it exists: a question about the images as a set is a claim
    /// about more than one of them, and merging separate readings cannot produce one — the merge
    /// prompt forbids exactly that. Asked for by name, it is one call again.
    /// </summary>
    [Fact]
    public async Task Images_asked_for_together_stay_one_call()
    {
        var client = new CountingClient();

        await new LocalTasks(client).ReadImageAsync(
            Images(3),
            "compare these screenshots",
            LocalTaskProfile.VisualAnalysis,
            model: null,
            together: true,
            TestContext.Current.CancellationToken);

        Assert.Equal([3], client.ImagesPerCall);
    }

    /// <summary>
    /// The counter the whole change exists for: one step per image, carrying the position, and
    /// none of the together form's record — which names a cargo this call does not have.
    /// </summary>
    [Fact]
    public async Task The_counted_form_reports_one_step_per_image()
    {
        var seen = new RecordingObserver();

        await new LocalTasks(new CountingClient(), seen).ReadImageAsync(
            Images(3),
            "transcribe the error text",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [new ReadingImage(1, 3), new ReadingImage(2, 3), new ReadingImage(3, 3)],
            seen.Steps.OfType<ReadingImage>());
        Assert.Empty(seen.Steps.OfType<ReadingImages>());
    }

    /// <summary>
    /// And the together form keeps the record it was given: there is nothing to count inside one
    /// call, so the console names the cargo on the heartbeat instead.
    /// </summary>
    [Fact]
    public async Task The_together_form_reports_its_count_once()
    {
        var seen = new RecordingObserver();

        await new LocalTasks(new CountingClient(), seen).ReadImageAsync(
            Images(3),
            "compare these screenshots",
            LocalTaskProfile.VisualAnalysis,
            model: null,
            together: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new ReadingImages(3), Assert.Single(seen.Steps.OfType<ReadingImages>()));
        Assert.Empty(seen.Steps.OfType<ReadingImage>());
    }

    /// <summary>
    /// The merge is a whole model call. Without a step for it the console goes silent under the
    /// line naming the last image, which reads as that image hanging.
    /// </summary>
    [Fact]
    public async Task The_merge_says_it_is_merging()
    {
        var seen = new RecordingObserver();

        await new LocalTasks(new CountingClient(), seen).ReadImageAsync(
            Images(3),
            "transcribe the error text",
            model: null,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(seen.Steps.OfType<MergingPartials>());
    }

    private string[] Images(int count) => Enumerable.Range(0, count)
        .Select(index =>
        {
            var path = Path.Combine(_work, $"shot-{index}.png");
            File.WriteAllBytes(path, CraftPngHeader(64, 64));
            return path;
        })
        .ToArray();

    private static byte[] CraftPngHeader(int width, int height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private sealed class RecordingObserver : ILocalRunObserver
    {
        public List<LocalRunStep> Steps { get; } = [];

        public void Report(LocalRunStep step) => Steps.Add(step);
    }

    /// <summary>Records what each call carried, and answers so the calls can be told apart.</summary>
    private sealed class CountingClient : ILocalModelClient
    {
        public List<int> ImagesPerCall { get; } = [];

        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<string>> RoutedChatAsync(
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
            ImagesPerCall.Add(imagesBase64?.Count ?? 0);
            return Task.FromResult(new LocalJobResult<string>(
                $"reply {ImagesPerCall.Count}",
                new LocalUsageReceipt(
                    Guid.NewGuid(),
                    "local-lm",
                    "chat",
                    "qwen3.5:9b",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    prompt.Length,
                    prompt.Length / 4,
                    null,
                    null,
                    null,
                    new LocalRoutingReceipt(
                        profile,
                        "qwen3.5:9b",
                        requestedContextTokens,
                        WasCold: false,
                        UsedFallback: false,
                        ValidatorResult: "none:pass",
                        EstimatedGrossCloudTokensSaved: prompt.Length / 4,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: prompt.Length / 4,
                        IsExperimentalAttempt: false))));
        }

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
    }
}
