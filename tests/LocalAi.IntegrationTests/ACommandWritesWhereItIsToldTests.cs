namespace LocalAi.IntegrationTests;

/// <summary>
/// The two commands that take their writer as a parameter must not also reach for the process
/// console, and this reads their source to say so.
///
/// A behavioural test cannot cover it. Every one of the four places that got this wrong sits past
/// the point where an embedding backend is required, and the suite never starts one — so a test
/// driving <c>ExecuteAsync</c> stops before reaching them. What could be tested was the helpers,
/// and they were: each has a test calling it directly with its own <c>StringWriter</c>, each was
/// green, and all four call sites still wrote to <c>Console.Error</c>. The tests proved the
/// helpers and said nothing about the path to them.
///
/// So this is a guard on the text, in the shape <c>ThemeReferencesAreDynamicTests</c> already
/// uses for XAML: it fails the build on a literal rather than on a behaviour. It is the weaker
/// kind of test and it is the one that would have caught this.
/// </summary>
public sealed class ACommandWritesWhereItIsToldTests
{
    [Theory]
    [InlineData("CodeSearchSyncCommand.cs")]
    [InlineData("SemanticNavigationCommand.cs")]
    public void A_command_that_is_handed_a_writer_never_names_the_console(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "LocalAi.Cli", fileName);
        var offending = File.ReadAllLines(path)
            .Select((text, index) => (Line: index + 1, Text: text))
            // Comments are allowed to name it: explaining why a parameter is not Console.Error
            // is the point of those sentences. Only code is under the rule. Block comments are
            // not stripped, because neither file uses them.
            .Where(entry => !entry.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(entry => entry.Text.Contains("Console.", StringComparison.Ordinal))
            .Select(entry => $"{fileName}:{entry.Line}:{entry.Text.Trim()}")
            .ToArray();

        Assert.True(
            offending.Length == 0,
            "A command with a writer parameter still writes to the process console:\n" +
            string.Join("\n", offending));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
