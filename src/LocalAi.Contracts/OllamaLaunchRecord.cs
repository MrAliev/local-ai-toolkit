using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAi.Contracts;

/// <summary>
/// The Ollama executable an installation established, so the broker can start it again later.
/// </summary>
/// <remarks>
/// It is written rather than looked up because looking one up at the moment of use is how a
/// background process ends up running whatever happens to answer to the name. The installer knows
/// which file it validated — against the uninstall entry, its approved directories and its
/// signed identity — and that is the only one worth starting unattended.
///
/// Ollama installs into a directory the user can write to, so the ACL check that guards the
/// winget executable cannot be reused here: it would reject an ordinary installation. Recording
/// the path the installer verified is what replaces it.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OllamaLaunchRecord(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ExecutablePath,
    string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelsDirectory = null)
{
    public const string FileName = "ollama-launch.json";

    public const int CurrentSchemaVersion = 2;
}

/// <summary>
/// Where that record lives, beside the other installation-wide documents in the runtime root so
/// the installer writes one copy and the broker reads it.
///
/// A missing, empty or malformed file reads as null. There is no default to fall back to: the
/// point of the record is that somebody verified this path, and a machine with no record is a
/// machine where nothing may be started.
/// </summary>
public sealed class OllamaLaunchRecordStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _path;

    public OllamaLaunchRecordStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _path = Path.Combine(runtimeRoot, OllamaLaunchRecord.FileName);
    }

    public OllamaLaunchRecord? Read()
    {
        try
        {
            EnsureNoLinks(_path);
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > 65536) return null;
            var record = JsonSerializer.Deserialize<OllamaLaunchRecord>(input, SerializerOptions);
            return record is null ||
                record.SchemaVersion is not (1 or 2) ||
                string.IsNullOrWhiteSpace(record.ExecutablePath) ||
                !Path.IsPathFullyQualified(record.ExecutablePath) ||
                (record.SchemaVersion == 1 ? record.ModelsDirectory is not null : !IsLocalModelsPath(record.ModelsDirectory))
                ? null
                : record;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuses a path that is not fully qualified, because a relative one would be resolved
    /// against whatever directory the broker happened to be started in.
    /// </summary>
    public void Save(string executablePath, string? version)
        => Write(executablePath, version, Read()?.ModelsDirectory);

    public void Save(string executablePath, string? version, string modelsDirectory)
    {
        ValidateModelsDirectory(modelsDirectory);
        Write(executablePath, version, Path.GetFullPath(modelsDirectory));
    }

    private void Write(string executablePath, string? version, string? modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The recorded Ollama path must be fully qualified.",
                nameof(executablePath));
        }

        EnsureNoLinks(_path);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new OllamaLaunchRecord(modelsDirectory is null ? 1 : OllamaLaunchRecord.CurrentSchemaVersion,
                    Path.GetFullPath(executablePath), version, modelsDirectory), SerializerOptions);
                stream.Flush(true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Never substitute the default store when an explicitly selected local store is unavailable.</summary>
    public static void ValidateModelsDirectory(string directory)
    {
        if (!IsLocalModelsPath(directory)) throw new ArgumentException("The model directory must be an absolute local path.", nameof(directory));
        EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The configured model directory is unavailable.");
    }

    private static bool IsLocalModelsPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal);

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The configured path contains a link.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
