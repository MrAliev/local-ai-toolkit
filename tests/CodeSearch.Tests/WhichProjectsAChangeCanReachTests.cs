using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// Which projects a saved file can affect, decided without opening anything.
///
/// The semantic phase reparses the whole solution for one saved file: 414 projects and 7057 files,
/// 314.4 s, on the solution that reported #519. Opening only what the change can reach is 40
/// projects and 9.0 s there. Deciding what it can reach is this type's whole job, and it does it
/// from the project files alone -- no MSBuild evaluation, no workspace.
/// </summary>
public sealed class WhichProjectsAChangeCanReachTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-project-graph-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_file_belongs_to_the_project_in_its_own_directory()
    {
        Project("Leaf");
        File.WriteAllText(Path.Combine(_root, "Leaf", "Thing.cs"), "class Thing;");

        var graph = ProjectGraph.Load(_root);

        Assert.Equal("Leaf/Leaf.csproj", graph.OwnerOf("Leaf/Thing.cs"));
    }

    /// <summary>
    /// The nearest project above it, not the outermost. A repository with a project at the root
    /// and another in a subdirectory would otherwise attribute every file to the root one.
    /// </summary>
    [Fact]
    public void A_file_belongs_to_the_nearest_project_above_it()
    {
        Project("Outer");
        Project(Path.Combine("Outer", "Inner"));
        Directory.CreateDirectory(Path.Combine(_root, "Outer", "Inner", "Deep"));
        File.WriteAllText(
            Path.Combine(_root, "Outer", "Inner", "Deep", "Thing.cs"),
            "class Thing;");

        var graph = ProjectGraph.Load(_root);

        Assert.Equal("Outer/Inner/Inner.csproj", graph.OwnerOf("Outer/Inner/Deep/Thing.cs"));
    }

    [Fact]
    public void A_file_under_no_project_at_all_belongs_to_none()
    {
        Project("Leaf");
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello");

        var graph = ProjectGraph.Load(_root);

        Assert.Null(graph.OwnerOf("README.md"));
    }

    /// <summary>
    /// Build output carries copies of source and of project files. Counting those would attribute
    /// a file to a project that is not built from it.
    /// </summary>
    [Fact]
    public void Build_output_is_not_a_project()
    {
        Project("Leaf");
        Project(Path.Combine("Leaf", "bin", "Debug", "Shadow"));

        var graph = ProjectGraph.Load(_root);

        Assert.Equal("Leaf/Leaf.csproj", graph.OwnerOf("Leaf/bin/Debug/Shadow/Thing.cs"));
    }

    /// <summary>
    /// The direction that matters for correctness: a change in C can alter how code in B and A
    /// binds, because they are the ones that reference it.
    /// </summary>
    [Fact]
    public void A_change_reaches_everything_that_references_it_transitively()
    {
        Project("A", references: ["../B/B.csproj"]);
        Project("B", references: ["../C/C.csproj"]);
        Project("C");

        var graph = ProjectGraph.Load(_root);

        Assert.Equal(
            ["A/A.csproj", "B/B.csproj", "C/C.csproj"],
            graph.Dependents(["C/C.csproj"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_change_does_not_reach_what_merely_shares_the_repository()
    {
        Project("A", references: ["../B/B.csproj"]);
        Project("B");
        Project("Unrelated");

        var graph = ProjectGraph.Load(_root);

        Assert.DoesNotContain("Unrelated/Unrelated.csproj", graph.Dependents(["B/B.csproj"]));
    }

    /// <summary>
    /// A reference cycle is invalid MSBuild and still appears in real trees. Terminating matters
    /// more than judging it.
    /// </summary>
    [Fact]
    public void A_cycle_terminates()
    {
        Project("A", references: ["../B/B.csproj"]);
        Project("B", references: ["../A/A.csproj"]);

        var graph = ProjectGraph.Load(_root);

        Assert.Equal(
            ["A/A.csproj", "B/B.csproj"],
            graph.Dependents(["A/A.csproj"]).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A worktree is a second checkout of the same repository sitting inside it. Counting its
    /// project files doubles every project and makes the owner of a file ambiguous -- the real
    /// tree found 844 projects where the repository has fewer, the surplus being one worktree.
    /// The file scanner already excludes them, and this reads what the index reads.
    /// </summary>
    [Fact]
    public void A_worktree_inside_the_repository_is_not_a_second_set_of_projects()
    {
        Project("Leaf");
        Project(Path.Combine(".claude", "worktrees", "branch-x", "Leaf"));

        var graph = ProjectGraph.Load(_root);

        Assert.Equal(["Leaf/Leaf.csproj"], graph.Projects);
    }

    private void Project(string directory, IReadOnlyList<string>? references = null)
    {
        var full = Path.Combine(_root, directory);
        Directory.CreateDirectory(full);
        var name = Path.GetFileName(directory);
        var items = references is null || references.Count == 0
            ? string.Empty
            : "  <ItemGroup>" + Environment.NewLine +
              string.Join(
                  Environment.NewLine,
                  references.Select(reference =>
                      "    <ProjectReference Include=\"" +
                      reference.Replace('/', Path.DirectorySeparatorChar) + "\" />")) +
              Environment.NewLine + "  </ItemGroup>" + Environment.NewLine;
        File.WriteAllText(
            Path.Combine(full, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">" + Environment.NewLine +
            items +
            "</Project>" + Environment.NewLine);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
