using LocalAi.Installer.Core.Releases;

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
    /// A separator the value ends with stays at the end, so it never becomes an empty entry
    /// and removal gives the value back as it was.
    /// </summary>
    public static string Add(string? path, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (Contains(path, directory))
        {
            return path!;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Normalize(directory);
        }

        return path.EndsWith(Separator, StringComparison.Ordinal)
            ? path + Normalize(directory) + Separator
            : path + Separator + Normalize(directory);
    }

    /// <summary>
    /// Ours, and nothing else, however the reader spelled theirs. Our slot goes with it rather
    /// than being left as the nameless directory `A;;B` means; empty entries already there stay.
    /// </summary>
    public static string Remove(string? path, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (string.IsNullOrEmpty(path))
        {
            return path ?? string.Empty;
        }

        var kept = path
            .Split(Separator)
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
    public static bool Register(string launcherDirectory, IUserPathStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        ArgumentNullException.ThrowIfNull(store);
        var before = store.Read();
        var after = Add(before, launcherDirectory);
        if (string.Equals(before ?? string.Empty, after, StringComparison.Ordinal))
        {
            return false;
        }

        store.Write(after);
        return true;
    }

    /// <summary>And takes it off again, with the same answer about whether anything changed.</summary>
    public static bool Unregister(string launcherDirectory, IUserPathStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        ArgumentNullException.ThrowIfNull(store);
        var before = store.Read();
        if (!Contains(before, launcherDirectory))
        {
            return false;
        }

        store.Write(Remove(before, launcherDirectory));
        return true;
    }

    /// <summary>
    /// Off the calling thread: the write is followed by a broadcast to every top-level window, and
    /// a window thread waiting on it stops answering its own.
    /// </summary>
    public static Task<bool> RegisterAsync(string launcherDirectory, IUserPathStore? store = null) =>
        Task.Run(() => Register(launcherDirectory, store ?? new WindowsUserPathStore()));

    public static Task<bool> UnregisterAsync(string launcherDirectory, IUserPathStore? store = null) =>
        Task.Run(() => Unregister(launcherDirectory, store ?? new WindowsUserPathStore()));

    /// <summary>
    /// This account's PATH as stored, not the one this process was started with: the process
    /// copy is a snapshot from before the installer ran and says nothing about what a console
    /// opened afterwards will see.
    /// </summary>
    public static string? Current() => new WindowsUserPathStore().Read();

    /// <summary>
    /// Two spellings of one directory. Windows paths do not differ by case, a trailing separator
    /// names the same place, a quoted entry is one somebody typed by hand, and an entry is read
    /// unexpanded, so %LOCALAPPDATA% may stand for part of it.
    /// </summary>
    private static bool Same(string entry, string directory) =>
        string.Equals(
            Normalize(Environment.ExpandEnvironmentVariables(entry)),
            Normalize(directory),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        value.Trim().Trim('"').TrimEnd('\\', '/');
}
