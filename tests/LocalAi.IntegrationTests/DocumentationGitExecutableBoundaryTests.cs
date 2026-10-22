using LocalAi.Documentation;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

[CollectionDefinition(DocumentationGitBoundaryCollection.Name, DisableParallelization = true)]
public sealed class DocumentationGitBoundaryCollection
{
    public const string Name = "Documentation Git executable boundary";
}

[Collection(DocumentationGitBoundaryCollection.Name)]
public sealed class DocumentationGitExecutableBoundaryTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(SkipUnless = nameof(IsWindows), Skip = "Exercises Windows implicit parent-CWD executable lookup.")]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Capture_uses_installed_Git_with_a_repository_cwd_and_trusted_PATH(bool subdirectory, bool decoy)
    {
        using var repo = new DocumentationRepositoryFixture(source: "public class A { public int Value => 42; }\n");
        var trustedGit = GitExecutableLocator.Resolve(repo.Root);
        var cwd = subdirectory ? Directory.CreateDirectory(Path.Combine(repo.Root, "caller")).FullName : repo.Root;
        if (decoy)
        {
            // A non-executable decoy makes accidental selection fail safely. The private
            // native positive control independently verifies that the old path can run code.
            File.WriteAllText(Path.Combine(cwd, "git.exe"), "Not an executable. Never run this repository fixture.");
        }

        var previousDirectory = Environment.CurrentDirectory;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousRestriction = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
        try
        {
            Environment.CurrentDirectory = cwd;
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(trustedGit));
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null);
            var input = DocumentationWorkspace.Capture(repo.Root, repo.Runtime,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(input.Units);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", previousRestriction);
        }
    }
}
