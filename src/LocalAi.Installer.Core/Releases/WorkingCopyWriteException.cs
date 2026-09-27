namespace LocalAi.Installer.Core.Releases;

/// <summary>
/// A copy into the installer's working folder that could not be written. The message is the file
/// system's own, so whatever shows only the message shows what it showed before.
/// </summary>
public sealed class WorkingCopyWriteException(string path, Exception cause)
    : IOException(cause.Message, cause)
{
    public string Path { get; } = path;
}
