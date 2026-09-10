using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// Opening the projects a change can reach instead of the solution.
///
/// On the solution that reported #519, one saved file opened 414 projects and parsed 7057 files,
/// 314.4 s. Opening one worker service and what it references was measured there at 40 projects
/// and 9.0 s.
/// </summary>
public sealed class OpeningOnlyWhatTheChangeCanReachTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-scoped-load-" + Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Project("Leaf");
        Project("Middle", references: ["../Leaf/Leaf.csproj"]);
        Project("Unrelated");
        // Alphabetically Alpha precedes Zebra, so the project that pulls Zebra in as a
        // dependency is opened first and Zebra is already there when it is asked for by name.
        Project("Alpha", references: ["../Zebra/Zebra.csproj"]);
        Project("Zebra");
        File.WriteAllText(
            Path.Combine(_root, "Fixture.sln"),
            string.Empty);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The whole point: a project the change cannot reach is not opened. `Unrelated` shares the
    /// tree and nothing else.
    /// </summary>
    [Fact]
    public async Task Only_the_named_projects_and_what_they_reference_are_opened()
    {
        await using var loaded = await RoslynSolutionLoader.LoadScopedAsync(
            _root,
            ["Middle/Middle.csproj"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        var names = loaded.ProjectNames().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Leaf", "Middle"], names);
    }

    [Fact]
    public async Task Naming_nothing_opens_nothing()
    {
        await using var loaded = await RoslynSolutionLoader.LoadScopedAsync(
            _root,
            [],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(loaded);
    }

    /// <summary>
    /// A path that names no project on disk is not a reason to fall back to the whole solution:
    /// that is the behaviour being replaced, and doing it silently would hide the mistake.
    /// </summary>
    [Fact]
    public async Task A_project_that_is_not_there_is_skipped_rather_than_widening_the_load()
    {
        await using var loaded = await RoslynSolutionLoader.LoadScopedAsync(
            _root,
            ["Middle/Middle.csproj", "Gone/Gone.csproj"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(
            ["Leaf", "Middle"],
            loaded.ProjectNames().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Two entry points where one references the other. Roslyn refuses to open a project that
    /// is already in the workspace -- 'X is already part of the workspace' -- and a change that
    /// touches a project and something it depends on is the ordinary case, not a corner.
    ///
    /// Found on a real solution rather than here: the single-entry-point test above passes
    /// either way, because there the referenced project arrives as a dependency and is never
    /// asked for by name.
    /// </summary>
    [Fact]
    public async Task A_project_already_pulled_in_as_a_reference_is_not_opened_again()
    {
        await using var loaded = await RoslynSolutionLoader.LoadScopedAsync(
            _root,
            ["Alpha/Alpha.csproj", "Zebra/Zebra.csproj"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(
            ["Alpha", "Zebra"],
            loaded.ProjectNames().Order(StringComparer.Ordinal));
    }

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
            "  <PropertyGroup>" + Environment.NewLine +
            "    <TargetFramework>net10.0</TargetFramework>" + Environment.NewLine +
            "  </PropertyGroup>" + Environment.NewLine +
            items +
            "</Project>" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(full, "Thing.cs"),
            "namespace " + directory + "; public class Thing { }" + Environment.NewLine);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
