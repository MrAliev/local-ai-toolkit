using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationIdentity(
    string RepositoryId,
    string DevCommit,
    string DevTree,
    string EmbeddingModel,
    int EmbeddingDimension,
    int ChunkFormatVersion,
    int IndexFormatVersion,
    int NormalizationVersion,
    int RankingVersion,
    int SemanticIndexVersion = 0)
{
    public bool CanReuseCorpusFrom(GenerationIdentity previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        return string.Equals(RepositoryId, previous.RepositoryId, StringComparison.Ordinal) &&
               string.Equals(
                   EmbeddingModel,
                   previous.EmbeddingModel,
                   StringComparison.Ordinal) &&
               EmbeddingDimension == previous.EmbeddingDimension &&
               ChunkFormatVersion == previous.ChunkFormatVersion &&
               IndexFormatVersion == previous.IndexFormatVersion &&
               NormalizationVersion == previous.NormalizationVersion;
    }

    public string Id
    {
        get
        {
            var values = new List<object>
            {
                RepositoryId,
                DevCommit,
                DevTree,
                EmbeddingModel,
                EmbeddingDimension,
                ChunkFormatVersion,
                IndexFormatVersion,
                NormalizationVersion,
                RankingVersion,
            };
            // Preserve IDs of all pre-SIDX generations. Once semantic data participates in a
            // generation, its format version becomes part of the immutable identity.
            if (SemanticIndexVersion > 0)
            {
                values.Add(SemanticIndexVersion);
            }

            var value = string.Join("\n", values);
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationManifest(
    GenerationIdentity Identity,
    string IndexFile,
    string IndexChecksum,
    DateTimeOffset PublishedAtUtc,
    string? SemanticIndexFile = null,
    string? SemanticIndexChecksum = null,
    IReadOnlyList<SemanticAdapterStatus>? SemanticAdapters = null);

/// <summary>
/// Which generation this repository answers from, and when that was decided.
///
/// Every member is required, as they are on <c>ProgressDocument</c> and <c>JobStateDocument</c>.
/// Without that the deserializer fills the constructor parameters with defaults, so a document as
/// empty as <c>{}</c> parsed into a pointer whose <c>GenerationId</c> was null — a valid pointer
/// to every caller, handed straight to <c>Path.Combine</c> and <c>ReadManifest</c>, neither of
/// which is guarded against null.
///
/// <c>DevTree</c> and <c>UpdatedAtUtc</c> are required too, though nothing reads them today: a
/// document missing them is not one this product wrote, and taking it for a pointer would be
/// guessing at what it meant.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationPointer(
    [property: JsonRequired] string GenerationId,
    [property: JsonRequired] string DevTree,
    [property: JsonRequired] DateTimeOffset UpdatedAtUtc);

public sealed record OverlayReadiness(
    string Worktree,
    string GenerationId,
    bool Ready);
