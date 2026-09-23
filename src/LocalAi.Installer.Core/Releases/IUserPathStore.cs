namespace LocalAi.Installer.Core.Releases;

/// <summary>The persisted PATH of this account, independent of the process snapshot.</summary>
public interface IUserPathStore
{
    string? Read();
    void Write(string value);
}

internal sealed class WindowsUserPathStore : IUserPathStore
{
    public string? Read() => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null;

    public void Write(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User);
    }
}
