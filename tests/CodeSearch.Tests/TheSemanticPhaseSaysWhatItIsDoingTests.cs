using CodeSearch.Core.Semantics;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// The longest phase of a sync said nothing at all. From `localai sync` to its first line:
/// 36.72 seconds of silence, all of it `dotnet restore` and `MSBuildWorkspace.OpenSolutionAsync`
/// — and hidden indexing is indistinguishable from a hung machine (#387).
///
/// The channel already existed: `LoadAsync` takes a diagnostic sink and the sync passes one that
/// prefixes `Roslyn: `. Nothing was written to it before the work began, so the sink only ever
/// carried failures.
///
/// This loads a real project, as `RealProjectSemanticsTests` does, because the lines have to
/// come from the path that actually runs MSBuild. It costs seconds; the alternative is asserting
/// against a fake that cannot go quiet.
/// </summary>
public sealed class TheSemanticPhaseSaysWhatItIsDoingTests
{
    /// <summary>
    /// Carries no PackageReference and no ProjectReference, so the restore is as short as this
    /// repository can make it while still being a real one.
    /// </summary>
    private const string ProjectDirectory = "LocalAi.Contracts";

    [Fact]
    public async Task The_load_says_it_started_before_it_goes_quiet()
    {
        using var reading = TestCulture.Reading("en");
        var root = Path.Combine(RepositoryRoot(), "src", ProjectDirectory);
        var said = new List<string>();

        await using var loaded = await RoslynSolutionLoader.LoadAsync(
            root,
            said.Add,
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);

        // Before the wait, not after it: a line printed once the restore is over explains a
        // silence the reader has already sat through.
        Assert.Contains(said, line => line.StartsWith("Loading C# from", StringComparison.Ordinal));

        // And after each step, with what it cost — which is how the next reader knows whether
        // 36 seconds was the restore or the open.
        Assert.Contains(said, line => line.StartsWith("Dependencies restored in", StringComparison.Ordinal));
        Assert.Contains(said, line => line.StartsWith("Opened ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Deliberately not an assertion about particular words — pinning the sentences would make
    /// this a copy of the resource file. That the two languages differ at all is the behaviour,
    /// and that the figures do not move between them is the rule about invariant numbers.
    /// </summary>
    [Fact]
    public void The_phase_lines_follow_the_reader_and_the_figures_do_not()
    {
        var english = (
            CodeSearch.Core.Resources.IndexText.RoslynLoading(@"R:\repo\x.slnx", 10),
            CodeSearch.Core.Resources.IndexText.RoslynRestored("21.4"),
            CodeSearch.Core.Resources.IndexText.RoslynOpened(26, "9.8"),
            CodeSearch.Core.Resources.IndexText.RoslynAnalysing(7, 26),
            CodeSearch.Core.Resources.IndexText.RoslynAnalysed(26, 1247, "41.2"));

        using var reading = TestCulture.Reading("ru");

        var russian = (
            CodeSearch.Core.Resources.IndexText.RoslynLoading(@"R:\repo\x.slnx", 10),
            CodeSearch.Core.Resources.IndexText.RoslynRestored("21.4"),
            CodeSearch.Core.Resources.IndexText.RoslynOpened(26, "9.8"),
            CodeSearch.Core.Resources.IndexText.RoslynAnalysing(7, 26),
            CodeSearch.Core.Resources.IndexText.RoslynAnalysed(26, 1247, "41.2"));

        Assert.NotEqual(english.Item1, russian.Item1, StringComparer.Ordinal);
        Assert.NotEqual(english.Item2, russian.Item2, StringComparer.Ordinal);
        Assert.NotEqual(english.Item3, russian.Item3, StringComparer.Ordinal);
        Assert.NotEqual(english.Item4, russian.Item4, StringComparer.Ordinal);
        Assert.NotEqual(english.Item5, russian.Item5, StringComparer.Ordinal);
        Assert.Contains("21.4", russian.Item2, StringComparison.Ordinal);
        Assert.Contains("26", russian.Item3, StringComparison.Ordinal);
        Assert.Contains("9.8", russian.Item3, StringComparison.Ordinal);
        Assert.Contains("7", russian.Item4, StringComparison.Ordinal);
        // Four digits, and the one figure in this phase where a group separator could appear: a
        // Russian machine must write 1247, never 1 247, because these lines are quoted verbatim
        // by agents and parsed by tests.
        Assert.Contains("1247", russian.Item5, StringComparison.Ordinal);
        Assert.Contains("41.2", russian.Item5, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
