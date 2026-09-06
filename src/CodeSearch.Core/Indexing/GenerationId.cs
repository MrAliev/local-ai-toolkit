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

    /// <summary>
    /// Whether a string has the shape <see cref="Id"/> produces: the hex of a SHA-256, lowercased,
    /// which is sixty-four lowercase hexadecimal characters and nothing else.
    ///
    /// Asked of ids that come back from disk rather than out of this type, because a generation id
    /// becomes a path segment — <c>Path.Combine(generationsRoot, generationId, …)</c> in
    /// <c>GenerationStore.IndexPath</c> and <c>ReadManifest</c>, both reached from a pointer read
    /// out of <c>current.json</c>. <c>Path.Combine</c> does not resolve <c>..</c> and drops the
    /// root when the second part is absolute, so without this the file opened as an index was
    /// chosen by the contents of that document.
    ///
    /// Case matters: the producer lowercases, so an uppercase id was written by something else,
    /// and on a case-insensitive file system it would resolve to a directory this product did not
    /// name.
    /// </summary>
    public static bool IsWellFormedId(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
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
///
/// Why this file is read with less ceremony than the launcher's pointer of the same name, which is
/// #402's question. Its protections are not all transferable:
///
/// <list type="bullet">
/// <item>The raw bytes, the 4096-byte cap, strict UTF-8 and the BOM check exist so that "the same
/// version" can mean "the same bytes": <c>CurrentPointerSnapshot</c> hashes what it read and
/// <c>CurrentPointerExpectation</c> compares SHA-256. This pointer's compare-and-swap is by value
/// — <see cref="GenerationStore.SetCurrent(GenerationManifest, GenerationPointer)"/> compares
/// <see cref="GenerationId"/> as a string (#199) — so byte-exactness has no part to play.</item>
/// <item><c>ActivationExclusiveLease</c> serialises activation across processes. The sync has its
/// own gate for that, and the expectation overload is the second guard for the gate a killed
/// process abandoned, as its own comment says.</item>
/// <item>Validating the value before it becomes a path segment does transfer, and did not exist.
/// <see cref="GenerationIdentity.IsWellFormedId"/> is that check, asked at the read, because
/// <c>LocalAiVersionName.IsSafe</c> is the reason the launcher's version can be a directory name
/// safely.</item>
/// </list>
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
