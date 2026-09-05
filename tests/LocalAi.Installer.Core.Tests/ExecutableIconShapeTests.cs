namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Every executable this repository ships wears the product's mark.
///
/// Not a matter of taste: the three background executables are seen in Task Manager, at 16px,
/// by somebody deciding what is safe to kill, and the launcher is the path registered in Claude
/// and Codex. One of them wearing the default .NET icon does not read as an oversight, it reads
/// as something that does not belong to this product.
///
/// Enforced rather than remembered, for the reason `McpToolInventoryTests` is: the icon is
/// declared once per project on purpose - `Directory.Build.props` reaches fifteen source
/// projects and ten test projects, none of which ever shows one - and a per-project declaration
/// is exactly the kind a ninth executable is added without.
/// </summary>
public sealed class ExecutableIconShapeTests
{
    private const string Icon = @"<ApplicationIcon>..\..\assets\localai.ico</ApplicationIcon>";

    [Fact]
    public void Every_executable_project_declares_the_mark()
    {
        var unmarked = ExecutableProjects()
            .Where(project => !File.ReadAllText(project).Contains(Icon, StringComparison.Ordinal))
            .Select(Relative)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unmarked.Length == 0,
            "These executables would ship without the product's icon: " +
            string.Join(", ", unmarked));
    }

    /// <summary>
    /// What the icon is, rather than whether it is there.
    ///
    /// A missing file needs no test: deleting it fails the build outright, with `CSC : error
    /// CS7064` naming the project - measured, by deleting it. What builds perfectly well is a
    /// single-frame icon, which is what every "export to .ico" produces, and the size Windows
    /// then resamples for the title bar is the one place this mark had to be hand-set. So the
    /// frame count is the assertion, and the existence check is only what makes its failure
    /// readable.
    /// </summary>
    [Fact]
    public void The_icon_carries_a_frame_for_every_size_windows_asks_for()
    {
        var icon = Path.Combine(RepositoryRoot(), "assets", "localai.ico");

        Assert.True(File.Exists(icon), icon + " is missing.");

        // An ICO header is a reserved word, a type - 1 for an icon - and the frame count.
        var header = File.ReadAllBytes(icon).AsSpan(0, 6);
        Assert.Equal(1, header[2] | (header[3] << 8));
        Assert.Equal(7, header[4] | (header[5] << 8));
    }

    private static IEnumerable<string> ExecutableProjects() =>
        Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src"),
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(project =>
            {
                var text = File.ReadAllText(project);
                return text.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal) ||
                    text.Contains("<OutputType>WinExe</OutputType>", StringComparison.Ordinal);
            });

    private static string Relative(string project) =>
        Path.GetRelativePath(RepositoryRoot(), project).Replace('\\', '/');

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

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
