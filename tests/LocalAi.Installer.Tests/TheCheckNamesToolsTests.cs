using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The system check titled a row with the detector's identifier, "GitHubCli", while the
/// dependencies page called the same tool "GitHub CLI".
/// </summary>
public sealed class TheCheckNamesToolsTests
{
    [Fact]
    public void A_tool_row_is_titled_as_the_dependencies_page_titles_the_tool()
    {
        var page = new DiagnosePageViewModel();
        var dependencies = new DependenciesPageViewModel().Dependencies;
        string Title(string id) => dependencies.Single(dependency => dependency.Id == id).Title;

        // Named as WindowsEnvironmentDetector names them.
        page.Load(HermeticDiagnosis.Supported(
            winGet: Missing("WinGet"),
            git: Missing("Git"),
            gitHubCli: Missing("GitHubCli")),
            registryAnswered: true);

        Assert.Equal(
            ["WinGet", Title("Git"), Title("GitHubCli")],
            page.Checks.Skip(1).Take(3).Select(check => check.Name));
    }

    [Fact]
    public void No_row_is_titled_with_what_the_detector_calls_a_tool()
    {
        var page = new DiagnosePageViewModel();

        page.Load(HermeticDiagnosis.Supported(
            winGet: Missing("detector-id"),
            git: Missing("detector-id"),
            gitHubCli: Missing("detector-id")),
            registryAnswered: true);

        Assert.DoesNotContain(page.Checks, check => check.Name == "detector-id");
    }

    private static DependencySnapshot Missing(string name) =>
        new(name, DependencyState.NotFound, null, null, null);
}
