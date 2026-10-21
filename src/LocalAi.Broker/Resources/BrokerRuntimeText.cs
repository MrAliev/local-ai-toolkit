using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Resources;

internal static class BrokerRuntimeText
{
    private static TextCatalogue Catalogue { get; } = new("LocalAi.Broker.Resources.BrokerRuntimeText", typeof(BrokerRuntimeText).Assembly);
    internal static string EmbeddingProofMismatch => Catalogue.Get(nameof(EmbeddingProofMismatch));
    internal static string ExternalContextMismatch(string model, int actual) =>
        Catalogue.Format(nameof(ExternalContextMismatch), model, actual);
}
