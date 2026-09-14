using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Documentation;

public sealed class BrokerDocumentationModel(ILocalModelClient client, Action<LocalUsageReceipt>? report = null) : IDocumentationModel
{
    public async Task<string> CompleteAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        var result = await client.ChatAsync(model, prompt, "Return only the requested structured document.",
            null, LocalJobPriority.Background, cancellationToken);
        report?.Invoke(result.Receipt);
        return result.Value;
    }

    public async Task<string> TranslateAsync(string english, CancellationToken cancellationToken)
    {
        var result = await client.RoutedChatAsync(LocalTaskProfile.PlainTranslation,
            "Translate the following fragment from English to Russian. Return only the translated fragment. " +
            "Preserve identifiers, code, URLs and placeholders exactly.\n\n" + english,
            "Translate faithfully without explanations or omissions.", null,
            new LocalWorkloadMetadata(english.Length, english.Length, 0, 0, 0, LocalDurationClass.Short),
            null, null, 8192, LocalJobPriority.Background, cancellationToken);
        report?.Invoke(result.Receipt);
        if (!TranslationValidator.ValidatePlain(english, result.Value).Passed)
            throw new DocumentationValidationException(["invalid_translation"]);
        return result.Value.Trim();
    }
}
