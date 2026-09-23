namespace LocalLm.Core;

/// <summary>OCR observations, not verified native text or word coordinates.</summary>
public sealed record PdfOcrEvidence(string Path, string Sha256, int TotalPages, PdfOcrPage[] Pages);
public sealed record PdfOcrPage(int Number, string Text, string Model);
