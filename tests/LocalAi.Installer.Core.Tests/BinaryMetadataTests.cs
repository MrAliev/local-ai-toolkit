using System.Reflection;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// A shipped binary names the product, its publisher and its copyright the same way Apps &amp;
/// features and the licence do. Code signing checks those fields, and a file whose properties
/// say nothing but its assembly name reads as somebody else's.
/// </summary>
public sealed class BinaryMetadataTests
{
    public static TheoryData<string> Assemblies =>
    [
        typeof(LocalAiPackageLayout).Assembly.GetName().Name!,
        typeof(UninstallRegistration).Assembly.GetName().Name!,
    ];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void An_assembly_names_the_product_and_its_publisher_as_apps_and_features_does(string name)
    {
        var assembly = Assembly.Load(name);

        Assert.Equal(UninstallRegistration.DisplayName, assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal(UninstallRegistration.Publisher, assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void An_assembly_carries_the_licence_copyright_line(string name)
    {
        var licence = File.ReadLines(Path.Combine(RepositoryRoot(), "LICENSE"))
            .Single(line => line.StartsWith("Copyright", StringComparison.Ordinal));

        Assert.Equal(licence, Assembly.Load(name).GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
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

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
