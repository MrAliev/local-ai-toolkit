using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>Only signed update documents, never a package or a credential.</summary>
public sealed record GitHubUpdateDocuments(string Tag, byte[] Manifest, byte[] Signature);

public interface IGitHubUpdateSource
{
    Task<GitHubUpdateDocuments> FetchAsync(string repository, CancellationToken cancellationToken);
}

public sealed class UpdateCheckSourceException(string failure) : Exception(failure)
{
    public string Failure { get; } = failure;
}

/// <summary>
/// Explicitly opted-in access through gh. Authentication stays inside gh, including redirects;
/// no token is requested, read, logged, or attached to the anonymous HTTP client's requests.
/// </summary>
public sealed class GitHubCliUpdateSource : IGitHubUpdateSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumSignatureBytes = 4 * 1024;
    private readonly Func<IReadOnlyList<string>, int, CancellationToken, Task<byte[]>> execute;

    public GitHubCliUpdateSource(
        Func<IReadOnlyList<string>, int, CancellationToken, Task<byte[]>>? execute = null) =>
        this.execute = execute ?? ExecuteAsync;

    public async Task<GitHubUpdateDocuments> FetchAsync(string repository, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant))
            throw new UpdateCheckSourceException(UpdateCheckFailure.InvalidResponse);

        var endpoint = $"repos/{repository}/releases/latest";
        var bytes = await ApiAsync(endpoint, "application/vnd.github+json", MaximumMetadataBytes,
            cancellationToken, "{tag_name:.tag_name,assets:[.assets[]|{id,name}]}").ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 256)
            throw new UpdateCheckSourceException(UpdateCheckFailure.InvalidResponse);
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var manifest = AssetId(ReleaseAssets.ManifestAsset);
        var signature = AssetId(ReleaseAssets.SignatureAsset);
        return new(tag,
            await ApiAsync($"repos/{repository}/releases/assets/{manifest}", "application/octet-stream",
                MaximumManifestBytes, cancellationToken).ConfigureAwait(false),
            await ApiAsync($"repos/{repository}/releases/assets/{signature}", "application/octet-stream",
                MaximumSignatureBytes, cancellationToken).ConfigureAwait(false));

        long AssetId(string name)
        {
            var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1 || !matches[0].GetProperty("id").TryGetInt64(out var id) || id <= 0)
                throw new UpdateCheckSourceException(UpdateCheckFailure.AssetsUnavailable);
            return id;
        }
    }

    private async Task<byte[]> ApiAsync(string endpoint, string accept, int maximumBytes,
        CancellationToken cancellationToken, string? filter = null)
    {
        List<string> arguments = ["api", "--hostname", "github.com", "--method", "GET",
            endpoint, "--header", "Accept: " + accept];
        if (filter is not null) arguments.AddRange(["--jq", filter]);
        var bytes = await execute(arguments, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > maximumBytes)
            throw new UpdateCheckSourceException(UpdateCheckFailure.InvalidResponse);
        return bytes;
    }

    internal static async Task<byte[]> ExecuteAsync(IReadOnlyList<string> arguments, int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(arguments) };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var oversized = 0;
        try
        {
            if (!process.Start()) throw new UpdateCheckSourceException(UpdateCheckFailure.AuthenticationUnavailable);
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumBytes, budget,
                () => Interlocked.Exchange(ref oversized, 1));
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 8 * 1024, budget,
                () => Interlocked.Exchange(ref oversized, 1));
            await Task.WhenAll(output, error, process.WaitForExitAsync(budget.Token))
                .WaitAsync(budget.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // Interpret bounded diagnostics, but never expose or persist their contents.
                var diagnostic = Encoding.UTF8.GetString(await error.ConfigureAwait(false));
                var failure = process.ExitCode == 4 || diagnostic.Contains("HTTP 401", StringComparison.Ordinal)
                    || diagnostic.Contains("HTTP 403", StringComparison.Ordinal)
                    ? UpdateCheckFailure.AuthenticationUnavailable
                    : diagnostic.Contains("HTTP 404", StringComparison.Ordinal)
                        ? UpdateCheckFailure.NoRelease : UpdateCheckFailure.UnexpectedStatus;
                throw new UpdateCheckSourceException(failure);
            }
            return await output.ConfigureAwait(false);
        }
        catch (Win32Exception) { throw new UpdateCheckSourceException(UpdateCheckFailure.AuthenticationUnavailable); }
        catch (InvalidDataException) { throw new UpdateCheckSourceException(UpdateCheckFailure.InvalidResponse); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref oversized) != 0)
        { throw new UpdateCheckSourceException(UpdateCheckFailure.InvalidResponse); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateCheckSourceException(UpdateCheckFailure.Timeout); }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments,
        string? searchPath = null, string? currentDirectory = null)
    {
        var executable = GitHubCliExecutableLocator.Find(searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            currentDirectory ?? Environment.CurrentDirectory)
            ?? throw new UpdateCheckSourceException(UpdateCheckFailure.AuthenticationUnavailable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["NO_COLOR"] = "1";
        return start;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes,
        CancellationTokenSource budget, Action exceeded)
    {
        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, budget.Token).ConfigureAwait(false);
                if (read == 0) return buffer.ToArray();
                if (buffer.Length + read > maximumBytes)
                {
                    exceeded();
                    throw new InvalidDataException("update_document_too_large");
                }
                buffer.Write(bytes, 0, read);
            }
        }
        catch
        {
            await budget.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
}
