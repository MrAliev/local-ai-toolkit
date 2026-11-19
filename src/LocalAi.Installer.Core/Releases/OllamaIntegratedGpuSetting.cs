using System.Runtime.Versioning;
using LocalAi.Contracts;
using Microsoft.Win32;

namespace LocalAi.Installer.Core.Releases;

public sealed record OllamaIntegratedGpuUserValue(string Text, bool Expandable);

/// <summary>The current Windows account's Ollama iGPU setting, independent of the wizard process.</summary>
[SupportedOSPlatform("windows")]
public sealed class OllamaIntegratedGpuSetting
{
    public static OllamaIntegratedGpuUserValue Enabled { get; } = new("1", false);

    private readonly string registrySubKey;
    private readonly Action announce;

    public OllamaIntegratedGpuSetting()
        : this(WindowsUserPathStore.DefaultSubKey, WindowsUserPathStore.AnnounceToOtherProcesses)
    {
    }

    internal OllamaIntegratedGpuSetting(string registrySubKey, Action announce)
    {
        this.registrySubKey = registrySubKey;
        this.announce = announce;
    }

    public OllamaIntegratedGpuUserValue? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registrySubKey);
        var value = key?.GetValue(OllamaEnvironment.IntegratedGpu, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return null;
        }

        return (value, key!.GetValueKind(OllamaEnvironment.IntegratedGpu)) switch
        {
            (string text, RegistryValueKind.String) => new(text, false),
            (string text, RegistryValueKind.ExpandString) => new(text, true),
            _ => throw new InvalidOperationException("The existing user setting is not a string."),
        };
    }

    public void Write(OllamaIntegratedGpuUserValue? value)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(registrySubKey, writable: true) ??
            Registry.CurrentUser.CreateSubKey(registrySubKey, writable: true))
        {
            if (value is null)
            {
                key.DeleteValue(OllamaEnvironment.IntegratedGpu, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(
                    OllamaEnvironment.IntegratedGpu,
                    value.Text,
                    value.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            }
        }

        announce();
    }
}
