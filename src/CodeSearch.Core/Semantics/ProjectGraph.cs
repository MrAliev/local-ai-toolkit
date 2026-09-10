using CodeSearch.Core.Indexing;
using System.Xml.Linq;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// Which projects a change can reach, read from the project files and nothing else.
///
/// The semantic phase reparses the whole solution whenever any source file changes: on the
/// solution that reported #519 that is 414 projects and 7057 files for one saved file, 314.4 s.
/// Opening only what the change can reach was measured there at 40 projects and 9.0 s. Deciding
/// which those are has to be cheaper than the thing it avoids, so this reads `.csproj` files as
/// XML -- no MSBuild evaluation, no workspace, no restore.
///
/// It answers two questions and no others: which project owns a file, and which projects
/// reference a given one. The second is the direction correctness depends on -- code binds to
/// what it references, so a change can only alter binding in projects that reference it.
/// </summary>
public sealed class ProjectGraph
{
    private readonly IReadOnlyList<string> _projects;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _referencedBy;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _references;

    private ProjectGraph(
        IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> referencedBy,
        IReadOnlyDictionary<string, IReadOnlyList<string>> references)
    {
        _projects = projects;
        _referencedBy = referencedBy;
        _references = references;
    }

    /// <summary>
    /// Every project under the root, and who references whom. Paths are relative to the root and
    /// separated by forward slashes, the way an index path is written.
    /// </summary>
    public static ProjectGraph Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        var projects = new List<string>();
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var forward = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // The scanner the index itself uses, rather than a second walk of the tree. It honours
        // .gitignore, skips build output, and skips a worktree nested in the repository -- and
        // a worktree is a full second checkout, so counting its project files would double every
        // project and make the owner of a file ambiguous.
        foreach (var relative in FileScanner.Enumerate(root)
                     .Select(path => path.Replace((char)92, '/'))
                     .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var absolute = Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar));

            projects.Add(relative);
            foreach (var referenced in References(root, absolute))
            {
                if (!edges.TryGetValue(referenced, out var dependents))
                {
                    dependents = [];
                    edges[referenced] = dependents;
                }

                dependents.Add(relative);
                if (!forward.TryGetValue(relative, out var referencedProjects))
                {
                    referencedProjects = [];
                    forward[relative] = referencedProjects;
                }

                referencedProjects.Add(referenced);
            }
        }

        projects.Sort(StringComparer.Ordinal);
        return new ProjectGraph(projects, Freeze(edges), Freeze(forward));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Freeze(
        Dictionary<string, List<string>> edges) =>
        edges.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Projects => _projects;

    /// <summary>
    /// The nearest project above the file, not the outermost one. A tree with a project at its
    /// root and another in a subdirectory would otherwise attribute every file to the root.
    /// </summary>
    public string? OwnerOf(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var path = relativePath.Replace((char)92, '/');
        var best = (string?)null;
        var bestDepth = -1;
        foreach (var project in _projects)
        {
            var directory = DirectoryOf(project);
            if (!path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var depth = directory.Count(character => character == '/');
            if (depth > bestDepth)
            {
                best = project;
                bestDepth = depth;
            }
        }

        return best;
    }

    /// <summary>
    /// The given projects and everything that references them, transitively. Inclusive, because a
    /// change reaches its own project first; and it terminates on a reference cycle, which is
    /// invalid MSBuild and still appears in real trees.
    /// </summary>
    public IReadOnlyCollection<string> Dependents(IEnumerable<string> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return Closure(projects, _referencedBy);
    }

    /// <summary>
    /// The given projects and everything they reference, transitively.
    ///
    /// Not a question about correctness but about cost: asking Roslyn for a project makes it open
    /// that project's references too, so this is how many projects a narrow rebuild will really
    /// open. Where that is most of the tree the solution is the cheaper route -- it restores once
    /// rather than once per entry point, which was 104.9 s of the 296.9 s measured in #519.
    /// </summary>
    public IReadOnlyCollection<string> Reaches(IEnumerable<string> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return Closure(projects, _references);
    }

    private static IReadOnlyCollection<string> Closure(
        IEnumerable<string> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> edges)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        foreach (var project in projects)
        {
            var normalized = project.Replace((char)92, '/');
            if (reached.Add(normalized))
            {
                pending.Enqueue(normalized);
            }
        }

        while (pending.Count > 0)
        {
            if (!edges.TryGetValue(pending.Dequeue(), out var next))
            {
                continue;
            }

            foreach (var project in next)
            {
                if (reached.Add(project))
                {
                    pending.Enqueue(project);
                }
            }
        }

        return reached;
    }

    private static IEnumerable<string> References(string root, string projectFile)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch (System.Xml.XmlException)
        {
            // A project file that does not parse references nothing this can see. The build will
            // say so far more usefully than a semantic index can.
            yield break;
        }

        var directory = Path.GetDirectoryName(projectFile)!;
        foreach (var element in document.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
            {
                continue;
            }

            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolved = Path.GetFullPath(Path.Combine(
                directory,
                include.Replace((char)92, Path.DirectorySeparatorChar)));
            if (resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                yield return Relative(root, resolved);
            }
        }
    }

    private static string Relative(string root, string absolute) =>
        Path.GetRelativePath(root, absolute).Replace((char)92, '/');

    private static string DirectoryOf(string relativeProjectPath)
    {
        var slash = relativeProjectPath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativeProjectPath[..slash];
    }
}
