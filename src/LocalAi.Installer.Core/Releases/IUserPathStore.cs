using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Releases;

/// <summary>The persisted PATH of this account, independent of the process snapshot.</summary>
public interface IUserPathStore
{
    string? Read();
    void Write(string value);
}

/// <summary>
/// The value as the registry holds it: read without expanding its variables, and written back in
/// the kind it was found in, so REG_EXPAND_SZ stays REG_EXPAND_SZ and %USERPROFILE% stays unexpanded.
/// </summary>
internal sealed class WindowsUserPathStore(string? registrySubKey = null, Action? announce = null) : IUserPathStore
{
    public const string DefaultSubKey = "Environment";

    private const string ValueName = "Path";

    private readonly string subKey = registrySubKey ?? DefaultSubKey;

    private readonly Action announce = announce ?? AnnounceToOtherProcesses;

    public string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using (var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true))
        {
            var kind = key.GetValue(ValueName) is not null &&
                key.GetValueKind(ValueName) == RegistryValueKind.ExpandString
                    ? RegistryValueKind.ExpandString
                    : RegistryValueKind.String;
            key.SetValue(ValueName, value, kind);
        }

        announce();
    }

    /// <summary>
    /// What Environment.SetEnvironmentVariable sends after writing a user variable, except that a
    /// window which has stopped answering is skipped rather than waited on.
    /// </summary>
    private static void AnnounceToOtherProcesses() =>
        SendMessageTimeout(
            new IntPtr(0xFFFF),
            0x001A,
            UIntPtr.Zero,
            "Environment",
            0x0002,
            1000,
            out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);
}
