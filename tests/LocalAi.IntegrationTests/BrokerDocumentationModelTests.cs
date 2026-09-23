using LocalAi.Contracts;
using LocalAi.Documentation;
using LocalLm.Core;

namespace LocalAi.IntegrationTests;

public sealed class BrokerDocumentationModelTests
{
    [Fact]
    public async Task Generation_and_translation_use_the_shared_client_and_report_both_receipts()
    {
        var client = new Client();
        var receipts = new List<LocalUsageReceipt>();
        var model = new BrokerDocumentationModel(client, receipts.Add);
        Assert.Equal("answer", await model.CompleteAsync("gpt-oss:20b", "facts", TestContext.Current.CancellationToken));
        Assert.Equal("Описание.", await model.TranslateAsync("Description.", TestContext.Current.CancellationToken));
        Assert.Equal("gpt-oss:20b", client.Model);
        Assert.Equal(LocalTaskProfile.PlainTranslation, client.Profile);
        Assert.Null(client.Override);
        Assert.Equal(2, receipts.Count);
    }

    [Theory]
    [InlineData("Translate the following fragment")]
    [InlineData("Description.")]
    public async Task Translation_prompt_leak_or_unchanged_prose_is_rejected(string answer)
    {
        var client = new Client { Translation = answer };
        await Assert.ThrowsAsync<DocumentationValidationException>(() =>
            new BrokerDocumentationModel(client).TranslateAsync("Description.", TestContext.Current.CancellationToken));
    }

    private sealed class Client : ILocalModelClient
    {
        internal string? Model; internal LocalTaskProfile? Profile; internal string? Override;
        internal string Translation = "Описание.";
        public Task<LocalJobResult<string>> ChatAsync(string model, string prompt, string? system, IReadOnlyList<string>? imagesBase64, LocalJobPriority priority, CancellationToken cancellationToken = default)
        { Model = model; return Task.FromResult(Result("answer", model)); }
        public Task<LocalJobResult<string>> RoutedChatAsync(LocalTaskProfile profile, string prompt, string? system, IReadOnlyList<string>? imagesBase64, LocalWorkloadMetadata workload, LocalWorkflowHint? workflow, string? modelOverride, int? requestedContextTokens, LocalJobPriority priority, CancellationToken cancellationToken = default)
        { Profile = profile; Override = modelOverride; return Task.FromResult(Result(Translation, "translategemma:12b")); }
        private static LocalJobResult<string> Result(string value, string model) => new(value, new LocalUsageReceipt(Guid.NewGuid(), "documentation", "chat", model, TimeSpan.Zero, TimeSpan.FromSeconds(3), 100, 0, null, null, null));
        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(string model, int contextTokens, string catalogVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(string model, string catalogVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(LocalTaskProfile profile, string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(LocalTaskProfile profile, string model, ExperimentOwnerAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
