using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Models;

/// <summary>
/// Ollama's model store, read from disk: asking Ollama or the broker which models are there would
/// start them. <c>IsDefault</c> marks Ollama's own default folder, which Ollama makes
/// when it first runs; one somebody chose may simply be unavailable.
/// </summary>
public sealed record OllamaModelStore(string Directory, bool IsDefault)
{
    private const string DefaultRegistry = "registry.ollama.ai";

    private const string LibraryNamespace = "library";

    /// <summary>
    /// The store the broker starts Ollama with: the folder its launch record names, else the one
    /// OLLAMA_MODELS names, else Ollama's default under the profile. Null when that is unknowable.
    /// </summary>
    public static OllamaModelStore? Locate(string runtimeRoot, string? environmentModels, string? userProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (new OllamaLaunchRecordStore(runtimeRoot).Read()?.ModelsDirectory is { } named)
        {
            return new(named, IsDefault: false);
        }

        if (!string.IsNullOrWhiteSpace(environmentModels))
        {
            return Path.IsPathFullyQualified(environmentModels) ? new(environmentModels, IsDefault: false) : null;
        }

        return !string.IsNullOrWhiteSpace(userProfile) && Path.IsPathFullyQualified(userProfile)
            ? new(Path.Combine(userProfile, ".ollama", "models"), IsDefault: true)
            : null;
    }

    /// <summary>Whether the store holds any of these models, or null when it cannot be read.</summary>
    public bool? HoldsAny(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var manifests = Path.Combine(Directory, "manifests");
        try
        {
            if (!System.IO.Directory.Exists(manifests))
            {
                return System.IO.Directory.Exists(Directory) || IsDefault ? false : null;
            }

            // Listing it is what proves it can be read: File.Exists answers false for a file it may not open.
            _ = System.IO.Directory.EnumerateFileSystemEntries(manifests).Any();
            return tags.Any(tag => ManifestPath(manifests, tag) is { } path && File.Exists(path));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Where Ollama keeps a model's manifest: registry, namespace, name, then tag.</summary>
    private static string? ManifestPath(string manifests, string tag)
    {
        var separator = tag.LastIndexOf(':');
        var (name, version) = separator > tag.LastIndexOf('/')
            ? (tag[..separator], tag[(separator + 1)..])
            : (tag, "latest");
        var parts = name.Split('/');
        string[] qualified = parts.Length switch
        {
            1 => [DefaultRegistry, LibraryNamespace, parts[0]],
            2 => [DefaultRegistry, .. parts],
            _ => parts,
        };
        string[] segments = [.. qualified, version];
        return segments.All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            ? Path.Combine([manifests, .. segments])
            : null;
    }
}
