using CodeSearch.Core.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeSearch.Tests;

/// <summary>
/// The C# analysis is the one step of the semantic phase where "project 7 of 26" is a fact: the
/// project count is known before the first iteration, and every document inside is awaited one at
/// a time. `dotnet restore` and `OpenSolutionAsync` have no such number, which is why #387 was
/// split rather than answered in one place, and this half of it printed nothing at all (#420).
///
/// The reporting is throttled by elapsed time, so both tests here drive a clock rather than a
/// machine: one that never moves and one that moves past the interval on every reading. Neither
/// waits, and neither says anything about how fast this computer is.
/// </summary>
public sealed class TheAnalysisSaysHowFarItIsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-analysis-progress-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A three-second analysis has nothing to report along the way, and reporting anyway would
    /// put a line on every small repository for no reason. What it still owes is the closing
    /// figure, which is the only place the file count is ever said.
    /// </summary>
    [Fact]
    public async Task An_analysis_that_finishes_quickly_says_only_what_it_cost()
    {
        var lines = new List<string>();

        await new RoslynSemanticIndexer(new FrozenClock()).BuildAsync(
            Solution(),
            _root,
            Identity(),
            lines.Add,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(lines, line => line.Contains("2/2", StringComparison.Ordinal));
        var closing = Assert.Single(lines);
        Assert.StartsWith("Analysed 2 projects and ", closing, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a slow one becomes the useful line of the phase. The fraction closes against the count
    /// `RoslynOpened` printed a moment earlier, so the reader is not asked to hold two numbers.
    /// </summary>
    [Fact]
    public async Task An_analysis_that_takes_time_says_which_project_it_is_on()
    {
        var lines = new List<string>();

        await new RoslynSemanticIndexer(new ClockPastTheInterval()).BuildAsync(
            Solution(),
            _root,
            Identity(),
            lines.Add,
            cancellationToken: TestContext.Current.CancellationToken);

        // One per project, then the closing line: the fixture has two projects.
        Assert.Equal(3, lines.Count);
        Assert.Equal("Analysed 1/2 projects.", lines[0]);
        Assert.Equal("Analysed 2/2 projects.", lines[1]);
        Assert.StartsWith("Analysed 2 projects and ", lines[2], StringComparison.Ordinal);
    }

    /// <summary>A build that is not asked to report says nothing and costs nothing.</summary>
    [Fact]
    public async Task An_analysis_with_nowhere_to_report_still_builds()
    {
        var index = await new RoslynSemanticIndexer(new ClockPastTheInterval()).BuildAsync(
            Solution(),
            _root,
            Identity(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(index.Documents);
    }

    /// <summary>Never moves, so nothing is ever due.</summary>
    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// Moves a second past the interval on every reading, so every project is due. Six seconds
    /// rather than five so the comparison is never on the boundary.
    /// </summary>
    private sealed class ClockPastTheInterval : TimeProvider
    {
        private long _now;

        public override long GetTimestamp() =>
            _now += (long)(RoslynSemanticIndexer.ReportInterval.TotalSeconds + 1) *
                TimeSpan.TicksPerSecond;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private static SemanticIndexBuildIdentity Identity() =>
        new("repository", "generation", "tree", null, "commit", DateTime.UnixEpoch);

    /// <summary>Two projects, one source file each: the count is what these tests are about.</summary>
    private Solution Solution()
    {
        Directory.CreateDirectory(_root);
        var workspace = new AdhocWorkspace();
        var first = AddProject(workspace.CurrentSolution, "First");
        var solution = first.Solution.AddDocument(
            DocumentId.CreateNewId(first.Id),
            "One.cs",
            SourceText.From("namespace First; public sealed class One { }"),
            filePath: Path.Combine(_root, "First", "One.cs"));
        var second = AddProject(solution, "Second");
        return second.Solution.AddDocument(
            DocumentId.CreateNewId(second.Id),
            "Two.cs",
            SourceText.From("namespace Second; public sealed class Two { }"),
            filePath: Path.Combine(_root, "Second", "Two.cs"));
    }

    /// <summary>
    /// Each project gets a file path, because the loop orders by it: without one the order is
    /// whatever the workspace returns, and `1/2` would name a different project between runs.
    /// </summary>
    private Project AddProject(Solution solution, string name)
    {
        var id = ProjectId.CreateNewId(name);
        return solution.AddProject(ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            filePath: Path.Combine(_root, name, name + ".csproj"))).GetProject(id)!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
