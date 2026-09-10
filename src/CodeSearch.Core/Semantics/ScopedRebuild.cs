namespace CodeSearch.Core.Semantics;

/// <summary>
/// Which projects to reopen, and whether the edit could have changed how anything else binds.
/// </summary>
/// <param name="Projects">
/// Everything the change reaches -- the answer to the question this type exists to ask.
/// </param>
/// <param name="EntryPoints">
/// The subset worth naming to Roslyn, which opens a project's references along with it and
/// restores once per name it is given. The two open the same set; only the restore count differs.
/// </param>
public sealed record ScopedRebuildPlan(
    IReadOnlyList<string> Projects,
    bool SurfaceChanged,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> EntryPoints);

/// <summary>
/// How much of the tree a save has to reopen.
///
/// Today the answer is always "all of it": one saved file reparses the solution, 414 projects and
/// 7057 files on the tree that reported #519, 314.4 s. There are three better answers and one of
/// them is the old one.
///
/// An edit inside a body reaches only the projects owning the edited files -- a body is invisible
/// from outside, so nothing else can bind differently. An edit that changes what a file declares
/// reaches everything that references those projects, which on a real tree can still be most of it
/// when an aggregator like an Aspire AppHost sits in the closure. And a change this cannot judge
/// reaches everything, by declining to plan: the full rebuild is the safe answer and it is what
/// happens today, so refusing costs nothing that is not already being paid.
/// </summary>
public static class ScopedRebuild
{
    /// <summary>
    /// The share of a repository's projects beyond which the solution is the cheaper route.
    /// Asking Roslyn for a project opens everything it references, and every entry point restores
    /// separately: on the solution that reported #519, 373 projects opened that way took 123.5 s
    /// after a 104.9 s restore, while all 414 opened through the solution took 40.9 s after one.
    /// The two meet at about a quarter of that tree.
    /// </summary>
    private const int WideRebuildShare = 4;

    /// <summary>
    /// Whether running this plan really is cheaper than reopening the solution.
    ///
    /// A plan is narrow in what it has to recompute and not necessarily in what it has to open:
    /// one project whose references reach an Aspire AppHost brings most of the tree with it. Where
    /// it does, the solution wins -- it restores once instead of once per entry point.
    /// </summary>
    public static bool NarrowerThanTheSolution(ScopedRebuildPlan plan, ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Reaches(plan.Projects).Count * WideRebuildShare < graph.Projects.Count;
    }

    /// <summary>
    /// A plan, or nothing at all when the change cannot be judged from source: a language whose
    /// declarations this cannot read, a file belonging to no project, or a file whose earlier
    /// version could not be fetched. Each of those is a reason to rebuild everything rather than
    /// to guess, and one of them among many settles the whole plan -- the rebuild is one
    /// operation and half of it cannot be scoped.
    /// </summary>
    /// <param name="currentText">The working copy, or null where the file is gone.</param>
    /// <param name="baseText">
    /// The version the published index was built from, or null where the file is new.
    /// </param>
    public static ScopedRebuildPlan? Plan(
        IReadOnlyList<string> changedPaths,
        ProjectGraph graph,
        Func<string, string?> currentText,
        Func<string, string?> baseText)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(currentText);
        ArgumentNullException.ThrowIfNull(baseText);
        if (changedPaths.Count == 0)
        {
            return null;
        }

        var owners = new List<string>();
        var surfaceChanged = false;
        foreach (var path in changedPaths)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var owner = graph.OwnerOf(path);
            if (owner is null)
            {
                return null;
            }

            owners.Add(owner);
            var now = currentText(path);
            var before = baseText(path);
            if (now is null || before is null)
            {
                // Appearing and disappearing are both surface changes: a new file declares names
                // that could shadow ones another file was resolving elsewhere, and a deleted one
                // takes names away that something was resolving to.
                surfaceChanged = true;
                continue;
            }

            surfaceChanged |= !DeclaredSurface.Unchanged(before, now);
        }

        var projects = owners.Distinct(StringComparer.OrdinalIgnoreCase);
        var reached = (surfaceChanged ? graph.Dependents(projects) : projects.ToArray())
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToArray();
        return new ScopedRebuildPlan(
            reached,
            surfaceChanged,
            changedPaths,
            graph.Roots(reached));
    }
}
