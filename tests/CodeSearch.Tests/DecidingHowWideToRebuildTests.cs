using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// How much of the tree a save has to reopen.
///
/// Three answers. An edit inside a body reaches only the projects that own the edited files. An
/// edit that changes what a file declares reaches everything that references those projects. And
/// anything this cannot judge reaches everything, by refusing to plan at all -- the full rebuild is
/// the safe answer, and it is the one that happens today.
/// </summary>
public sealed class DecidingHowWideToRebuildTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-scoped-plan-" + Guid.NewGuid().ToString("N"));

    public DecidingHowWideToRebuildTests()
    {
        Project("Leaf");
        Project("Middle", references: ["../Leaf/Leaf.csproj"]);
        Project("Top", references: ["../Middle/Middle.csproj"]);
    }

    [Fact]
    public void An_edit_inside_a_body_reaches_only_its_own_project()
    {
        var plan = ScopedRebuild.Plan(
            ["Leaf/Thing.cs"],
            ProjectGraph.Load(_root),
            _ => Source("return 1;"),
            _ => Source("return 2;"));

        Assert.NotNull(plan);
        Assert.False(plan.SurfaceChanged);
        Assert.Equal(["Leaf/Leaf.csproj"], plan.Projects);
    }

    [Fact]
    public void An_edit_that_changes_a_declaration_reaches_everything_that_references_it()
    {
        var plan = ScopedRebuild.Plan(
            ["Leaf/Thing.cs"],
            ProjectGraph.Load(_root),
            _ => Source("return 1;", extra: "public int Added() { return 0; }"),
            _ => Source("return 1;"));

        Assert.NotNull(plan);
        Assert.True(plan.SurfaceChanged);
        Assert.Equal(
            ["Leaf/Leaf.csproj", "Middle/Middle.csproj", "Top/Top.csproj"],
            plan.Projects.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A new file declares things nothing declared before, which can shadow a name another file
    /// was resolving elsewhere. Same for a deleted one, from the other side.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_file_that_appears_or_disappears_reaches_everything_that_references_it(bool added)
    {
        var plan = ScopedRebuild.Plan(
            ["Leaf/Thing.cs"],
            ProjectGraph.Load(_root),
            added ? _ => Source("return 1;") : _ => null,
            added ? _ => null : _ => Source("return 1;"));

        Assert.NotNull(plan);
        Assert.True(plan.SurfaceChanged);
    }

    /// <summary>
    /// The languages whose surface this cannot read. Refusing to plan is what keeps the answer
    /// honest: a `.xaml` or a `.ts` change is handled by the rebuild that exists today.
    /// </summary>
    [Theory]
    [InlineData("Leaf/View.xaml")]
    [InlineData("Leaf/app.ts")]
    [InlineData("Leaf/script.py")]
    [InlineData("Leaf/Leaf.csproj")]
    public void A_change_this_cannot_read_is_not_planned(string path)
    {
        var plan = ScopedRebuild.Plan(
            [path],
            ProjectGraph.Load(_root),
            _ => Source("return 1;"),
            _ => Source("return 1;"));

        Assert.Null(plan);
    }

    [Fact]
    public void A_file_belonging_to_no_project_is_not_planned()
    {
        var plan = ScopedRebuild.Plan(
            ["Loose/Thing.cs"],
            ProjectGraph.Load(_root),
            _ => Source("return 1;"),
            _ => Source("return 1;"));

        Assert.Null(plan);
    }

    [Fact]
    public void Nothing_changed_is_not_planned()
    {
        var plan = ScopedRebuild.Plan(
            [],
            ProjectGraph.Load(_root),
            _ => Source("return 1;"),
            _ => Source("return 1;"));

        Assert.Null(plan);
    }

    /// <summary>
    /// One unreadable change among readable ones decides for all of them: the rebuild is one
    /// operation, and half of it cannot be scoped.
    /// </summary>
    [Fact]
    public void One_change_this_cannot_read_settles_the_whole_plan()
    {
        var plan = ScopedRebuild.Plan(
            ["Leaf/Thing.cs", "Leaf/View.xaml"],
            ProjectGraph.Load(_root),
            _ => Source("return 1;"),
            _ => Source("return 1;"));

        Assert.Null(plan);
    }

    /// <summary>
    /// Narrow is not free. Roslyn opens a requested project's references with it and restores
    /// once per entry point, so a plan that ends up opening most of the tree costs more that way
    /// than opening the solution -- 373 projects in 123.5 s after a 104.9 s restore, against 414
    /// in 40.9 s after one, on the solution that reported #519.
    /// </summary>
    [Fact]
    public void A_plan_that_opens_a_small_part_of_the_tree_is_narrower_than_the_solution()
    {
        for (var index = 0; index < 5; index++)
        {
            Project("Other" + index);
        }

        var graph = ProjectGraph.Load(_root);
        var plan = ScopedRebuild.Plan(
            ["Leaf/Thing.cs"],
            graph,
            _ => Source("return 1;"),
            _ => Source("return 2;"));

        Assert.NotNull(plan);
        Assert.Single(graph.Reaches(plan.Projects));
        Assert.True(ScopedRebuild.NarrowerThanTheSolution(plan, graph));
    }

    [Fact]
    public void A_plan_that_opens_most_of_the_tree_is_not()
    {
        for (var index = 0; index < 5; index++)
        {
            Project("Other" + index);
        }

        var graph = ProjectGraph.Load(_root);
        var plan = ScopedRebuild.Plan(
            ["Top/Thing.cs"],
            graph,
            _ => Source("return 1;"),
            _ => Source("return 2;"));

        Assert.NotNull(plan);
        Assert.Equal(3, graph.Reaches(plan.Projects).Count);
        Assert.False(ScopedRebuild.NarrowerThanTheSolution(plan, graph));
    }

    private static string Source(string body, string extra = "") =>
        "namespace Demo; public class Thing { public int Go() { " + body + " } " + extra + " }";

    private void Project(string directory, IReadOnlyList<string>? references = null)
    {
        var full = Path.Combine(_root, directory);
        Directory.CreateDirectory(full);
        var items = references is null || references.Count == 0
            ? string.Empty
            : "  <ItemGroup>" + Environment.NewLine +
              string.Join(
                  Environment.NewLine,
                  references.Select(reference =>
                      "    <ProjectReference Include=" + '"' + reference + '"' + " />")) +
              Environment.NewLine + "  </ItemGroup>" + Environment.NewLine;
        File.WriteAllText(
            Path.Combine(full, directory + ".csproj"),
            "<Project Sdk=" + '"' + "Microsoft.NET.Sdk" + '"' + ">" + Environment.NewLine +
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
