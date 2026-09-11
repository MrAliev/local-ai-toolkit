using System.Text;

namespace LocalAi.Installer.Core.Agents;

/// <summary>
/// Writing and removing the `localai` skill, for whichever client is being configured. What is
/// ours is the block between the markers, not the file it sits in.
/// </summary>
internal static class SkillFilePlan
{
    public static void Add(
        List<AgentConfigurationFilePlan> files,
        string path,
        Func<string, byte[]> read,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        var before = read(path);
        var after = After(before);
        if (!before.SequenceEqual(Encoding.UTF8.GetBytes(after)))
        {
            files.Add(AgentConfigurationFileOperations.FilePlan(path, before, after, now));
        }
    }

    /// <summary>
    /// A file written before the markers existed is our own text without them, so it is
    /// rewritten rather than earning a second copy appended below the first. Anything else has
    /// the block put in or replaced, and keeps what surrounds it.
    /// </summary>
    private static string After(byte[] before)
    {
        if (before.Length == 0)
        {
            return ManagedInstructionBlock.SkillFile();
        }

        var content = AgentConfigurationFileOperations.DecodeUtf8(before);
        return WasOursBeforeTheMarkers(content)
            ? ManagedInstructionBlock.SkillFile()
            : ManagedInstructionBlock.Upsert(content, ManagedInstructionBlock.SkillBlock()).Content;
    }

    private static bool WasOursBeforeTheMarkers(string content) =>
        !content.Contains(ManagedInstructionBlock.BeginMarker, StringComparison.Ordinal) &&
        content.TrimStart().StartsWith("---", StringComparison.Ordinal) &&
        content.Contains("name: " + ManagedInstructionBlock.SkillName, StringComparison.Ordinal);

    /// <summary>
    /// Our block comes out; everything written around it stays. The file goes only when what is
    /// left is the frontmatter alone — a skill with no body is worse than no skill — and a file
    /// carrying no marker of ours is not touched at all.
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
        if (before.Length == 0)
        {
            return;
        }

        var content = AgentConfigurationFileOperations.DecodeUtf8(before);
        if (!content.Contains(ManagedInstructionBlock.BeginMarker, StringComparison.Ordinal))
        {
            return;
        }

        var after = ManagedInstructionBlock.Remove(content).Content;
        files.Add(ManagedInstructionBlock.IsFrontmatterOnly(after)
            ? AgentConfigurationFileOperations.DeletePlan(path, before, now)
            : AgentConfigurationFileOperations.FilePlan(path, before, after, now));
    }
}
