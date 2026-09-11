namespace LocalAi.Installer.Tests;

/// <summary>
/// The shipped markup, read from source. Two suites check things about the XAML that no test
/// can see through a running window, and both need to find the project first.
/// </summary>
internal static class InstallerMarkup
{
    internal static readonly string[] Windows =
        ["MainWindow.xaml", "StartWindow.xaml", "UninstallWindow.xaml"];

    internal static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    internal static string Directory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "src", "LocalAi.Installer");
        }
    }
}
