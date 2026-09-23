using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Releases;

public sealed record OfflineUninstallPlan(string Source, string Destination, long Bytes, string Sha256,
    string RegistryScope, string RegistrySubKey, UninstallEntry? Before);

public interface IOfflineUninstallRegistration
{
    string RegistrySubKey { get; }
    UninstallEntry? Read(InstallationLayout layout);
    UninstallEntry Register(InstallationLayout layout, string version, string installerSource);
}

public sealed class OfflineUninstallRegistration(string? registrySubKey = null) : IOfflineUninstallRegistration
{
    public string RegistrySubKey { get; } = registrySubKey ?? @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAi";

    public UninstallEntry? Read(InstallationLayout layout) => OperatingSystem.IsWindows()
        ? new UninstallRegistration(layout, RegistrySubKey).Read() : null;

    public UninstallEntry Register(InstallationLayout layout, string version, string installerSource)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new UninstallRegistration(layout, RegistrySubKey).Register(version, installerSource);
    }
}
