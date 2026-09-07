using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Repository;

public sealed class RepositoryManifestStore
{
    private readonly string _manifestPath;

    public RepositoryManifestStore(FsPath repositoryRuntimeRoot)
    {
        _manifestPath = repositoryRuntimeRoot.Combine("manifest.json").Value;
    }

    /// <summary>
    /// The same window as the progress record's, for the same reason: <c>Read</c> below opens
    /// through <c>File.ReadAllText</c>, and every reader of a repository's state — <c>repo
    /// status</c>, the MCP server, the next sync — goes through it.
    /// </summary>
    private static readonly TimeSpan ReplaceRetryBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Recomputable, and written as such: the manifest states the repository id, its mainline
    /// ref, the model and the worktrees, all of which a sync derives again from the repository
    /// itself.
    /// </summary>
    public void Save(RepositoryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            LocalAiJson.Strict);
        AtomicFile.WriteJson(
            _manifestPath,
            new ManifestDocument(
                1,
                manifest,
                Convert.ToHexString(SHA256.HashData(payload))),
            LocalAiJson.Strict,
            AtomicWrite.Recomputable.Retrying(ReplaceRetryBudget));
    }

    public RepositoryManifest? Read()
    {
        if (!File.Exists(_manifestPath))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<ManifestDocument>(
            File.ReadAllText(_manifestPath),
            LocalAiJson.Strict)
            ?? throw new InvalidDataException("Repository manifest is empty.");
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException("Repository manifest schema is unsupported.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            document.Manifest,
            LocalAiJson.Strict);
        byte[] declared;
        if (document.Checksum.Length != 64)
        {
            throw new InvalidDataException("Repository manifest checksum is malformed.");
        }

        try
        {
            declared = Convert.FromHexString(document.Checksum);
        }
        catch (FormatException error)
        {
            // A checksum that does not decode is the same category as one that does not
            // match — a corrupt manifest, answered by rebuilding it — not a raw
            // FormatException that reads as a bug in this code (#209/m7).
            throw new InvalidDataException(
                "Repository manifest checksum is malformed.",
                error);
        }

        if (!CryptographicOperations.FixedTimeEquals(declared, SHA256.HashData(payload)))
        {
            throw new InvalidDataException("Repository manifest checksum does not match.");
        }

        return document.Manifest;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ManifestDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] RepositoryManifest Manifest,
        [property: JsonRequired] string Checksum);
}
