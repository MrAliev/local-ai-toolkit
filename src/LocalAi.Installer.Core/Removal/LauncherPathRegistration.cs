namespace LocalAi.Installer.Core.Removal;

/// <summary>
/// The launcher's directory on the account's PATH, so the commands the installed instructions
/// offer can actually be typed.
///
/// Sixteen of them across `SKILL.md`, `CLAUDE.md` and `AGENTS.md` begin `localai-launcher.exe`,
/// and none of them ran: the launcher was never on the path, and the half of the toolkit those
/// commands reach is the documented way through a dead MCP server.
///
/// The user's PATH rather than the machine's. The whole installation lives under LOCALAPPDATA
/// and belongs to one account; writing the machine's would need elevation and would put one
/// account's launcher in front of everybody else's.
///
/// The string work is separated from the machine so it can be tested without one: what makes
/// this correct is what it does to a PATH somebody else wrote.
/// </summary>
public static class LauncherPathRegistration
{
    public const string Separator = ";";

    /// <summary>
    /// The directory, appended unless it is already there. Appended rather than prepended: this
    /// is not a tool that should win a name collision with something the reader put first.
    /// </summary>
    public static string Add(string? path, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (Contains(path, directory))
        {
            return path!;
        }

        return string.IsNullOrWhiteSpace(path)
            ? Normalize(directory)
            : path + Separator + Normalize(directory);
    }

    /// <summary>
    /// Ours, and nothing else, however the reader spelled theirs. Empty entries are dropped with
    /// it rather than left as the nameless directory `A;;B` means.
    /// </summary>
    public static string Remove(string? path, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (string.IsNullOrEmpty(path))
        {
            return path ?? string.Empty;
        }

        var kept = path
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !Same(entry, directory))
            .ToArray();
        return string.Join(Separator, kept);
    }

    public static bool Contains(string? path, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return !string.IsNullOrEmpty(path) &&
            path.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                .Any(entry => Same(entry, directory));
    }

    /// <summary>
    /// Puts the directory on this account's PATH. True when it was not already there, which is
    /// what the run reports: a reinstall over an installation changes nothing and should say so
    /// rather than claiming an effect.
    /// </summary>
    public static bool Register(string launcherDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        var before = Current();
        var after = Add(before, launcherDirectory);
        if (string.Equals(before ?? string.Empty, after, StringComparison.Ordinal))
        {
            return false;
        }

        Write(after);
        return true;
    }

    /// <summary>And takes it off again, with the same answer about whether anything changed.</summary>
    public static bool Unregister(string launcherDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        var before = Current();
        if (!Contains(before, launcherDirectory))
        {
            return false;
        }

        Write(Remove(before, launcherDirectory));
        return true;
    }

    /// <summary>
    /// This account's PATH as stored, not the one this process was started with: the process
    /// copy is a snapshot from before the installer ran and says nothing about what a console
    /// opened afterwards will see.
    /// </summary>
    public static string? Current() =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)
            : null;

    private static void Write(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User);
        }
    }

    /// <summary>
    /// Two spellings of one directory. Windows paths do not differ by case, a trailing separator
    /// names the same place, and a quoted entry is one somebody typed by hand.
    /// </summary>
    private static bool Same(string entry, string directory) =>
        string.Equals(Normalize(entry), Normalize(directory), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        value.Trim().Trim('"').TrimEnd('\\', '/');
}
