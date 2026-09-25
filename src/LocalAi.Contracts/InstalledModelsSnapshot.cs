using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// The model tags Ollama listed as installed, and when the broker looked.
///
/// The broker writes it whenever it lists the installed models and on a pass of its own;
/// <c>doctor</c> reads it, because a diagnostic that starts the broker to ask would answer a
/// question nobody asked (#633).
/// </summary>
public sealed record InstalledModelsSnapshot(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] DateTimeOffset CheckedAtUtc,
    [property: JsonRequired] IReadOnlyList<string> InstalledModels)
{
    public const string FileName = "installed-models.json";

    /// <summary>How often a running broker looks again when nothing else has made it look.</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Two passes, the update check's rule: a running broker looks once a pass, so a record
    /// older than this says nobody is looking any more.
    /// </summary>
    public static TimeSpan StaleAfter => PassInterval * 2;

    public bool IsStale(DateTimeOffset now) => now - CheckedAtUtc > StaleAfter;
}

public sealed class InstalledModelsSnapshotStore
{
    private readonly string _path;

    public InstalledModelsSnapshotStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = Path.Combine(Path.GetFullPath(runtimeRoot), InstalledModelsSnapshot.FileName);
    }

    public string FilePath => _path;

    /// <summary>The last record, or null when there is none or it cannot be read.</summary>
    public InstalledModelsSnapshot? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<InstalledModelsSnapshot>(
                File.ReadAllBytes(_path),
                LocalAiJson.Strict);
            return snapshot?.SchemaVersion == 1 ? snapshot : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(IReadOnlyList<string> installedModels, DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(installedModels);
        // Recomputable: the next listing writes it again.
        AtomicFile.WriteJson(
            _path,
            new InstalledModelsSnapshot(1, checkedAtUtc, installedModels),
            LocalAiJson.Strict,
            AtomicWrite.Recomputable);
    }
}
