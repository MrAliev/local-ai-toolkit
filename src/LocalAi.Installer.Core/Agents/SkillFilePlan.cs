using System.Text;

namespace LocalAi.Installer.Core.Agents;

/// <summary>
/// Writing and removing the `localai` skill file, for whichever client is being configured.
///
/// Shared rather than written once per adapter: both clients are handed the same bytes from
/// <see cref="ManagedInstructionBlock.SkillFile"/>, so both have to apply the same rule about
/// when that file may be deleted. Two copies of "delete only while it is still ours" is two
/// places for it to drift, and the drift destroys somebody's edit.
/// </summary>
internal static class SkillFilePlan
{
    /// <summary>
    /// The reference material, written where the client discovers a personal skill. The
    /// directory names the skill and the frontmatter repeats the name, so a reader and a matcher
    /// agree about what it is called.
    /// </summary>
    public static void Add(
        List<AgentConfigurationFilePlan> files,
        string path,
        Func<string, byte[]> read,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        var before = read(path);
        var after = ManagedInstructionBlock.SkillFile();
        if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(path, before, after, now));
        }
    }

    /// <summary>
    /// The skill file goes only while it is still the file this installer wrote. Somebody who
    /// edited it keeps their edit, because destroying somebody's writing is the worse of the two
    /// mistakes available here.
    ///
    /// What they do not get is a word about it: an edited skill is left on disk and no retained
    /// notice names it, so the uninstall is silently incomplete for exactly the person who cared
    /// enough to edit the file. Worth fixing; not fixed here.
    ///
    /// The comparison is against the current version's text, so a skill written by an older
    /// LocalAi and never refreshed is retained rather than deleted. That is a real limit, and
    /// the alternative — a table of every text ever shipped — buys less than it costs.
    /// </summary>
    public static void Remove(
        List<AgentConfigurationFilePlan> files,
        string path,
        Func<string, byte[]> read,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        var before = read(path);
        if (before.Length == 0 ||
            !before.SequenceEqual(Encoding.UTF8.GetBytes(ManagedInstructionBlock.SkillFile())))
        {
            return;
        }

        files.Add(AgentConfigurationFileOperations.DeletePlan(path, before, now));
    }
}
