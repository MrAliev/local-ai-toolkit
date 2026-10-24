using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Transactions;

/// <summary>The shared location for installer journals and run reports.</summary>
public static class InstallerLogDirectory
{
    public static string Default => FromLocalApplicationData(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify));

    public static string FromLocalApplicationData(string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            !Path.IsPathFullyQualified(localApplicationData))
        {
            throw new InvalidOperationException(
                "The default installer log directory requires an absolute user-data directory.");
        }

        return Path.Combine(localApplicationData, RemovalMatrix.JournalDirectoryName);
    }
}
