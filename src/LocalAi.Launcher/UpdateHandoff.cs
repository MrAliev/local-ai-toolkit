namespace LocalAi.Launcher;

/// <summary>
/// Runs the single-file updater outside the immutable version that it must replace.
/// The launcher owns this temporary directory until its child has exited.
/// </summary>
internal sealed class UpdateHandoff : IDisposable
{
    private readonly DirectoryInfo _directory;

    private UpdateHandoff(DirectoryInfo directory)
    {
        _directory = directory;
        ExecutablePath = Path.Combine(directory.FullName, "localai.exe");
    }

    public string ExecutablePath { get; }

    public static async Task<UpdateHandoff> CreateAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        UpdateHandoff? handoff = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            handoff = new UpdateHandoff(Directory.CreateTempSubdirectory("localai-update-"));
            // Keep the installed source stable while copying. Release builds embed managed
            // assemblies and the .NET runtime; update does not use the PDF/rendering sidecars.
            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                handoff.ExecutablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
            return handoff;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            handoff?.Dispose();
            throw new LauncherException(
                "update_handoff_failed",
                $"Could not stage the Local AI updater: {exception.Message}");
        }
        catch
        {
            handoff?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Cleanup must not hide the updater's result if another process briefly locks it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
