namespace LocalAi.Broker;

/// <summary>
/// Ollama does not have the model an embedding job asked for. A stable failure code of its own, so
/// a caller can say which model is missing without parsing Ollama's quoted response (#633).
/// </summary>
public sealed class EmbeddingModelNotInstalledException(string model, Exception innerException)
    : InvalidOperationException($"Embedding model '{model}' is not installed in Ollama.", innerException)
{
    public string Model { get; } = model;
}
