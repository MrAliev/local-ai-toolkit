namespace LocalAi.Installer.Core.Diagnosis;

/// <summary>
/// How a graphics adapter is named to a person.
///
/// One formatter, because there were two descriptions of the same adapter and only one of them
/// was readable: the system-check page said "NVIDIA GeForce RTX 5080 (16.3 GB dedicated)" while
/// the model recommendation said "Using dedicated GPU adapter 'PCI\VEN_10DE&amp;DEV_2C02&amp;…'",
/// and the second one is what reached the models page.
///
/// DXGI provides the adapter name, dedicated video memory and shared-memory ceiling.
/// The shared-memory ceiling describes capacity and may be higher than the current GPU budget.
/// </summary>
public static class GpuAdapterDisplay
{
    private const double BytesPerGigabyte = 1024d * 1024 * 1024;

    /// <summary>For a window. Follows the language the installer was told to speak.</summary>
    public static string Describe(GpuAdapterSnapshot adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var memory = adapter.DedicatedLocalBytes > 0
            ? string.Format(
                InstallerCulture.Pick(
                    " ({0:N1} GB dedicated)",
                    " ({0:N1} ГБ выделенной памяти)"),
                adapter.DedicatedLocalBytes / BytesPerGigabyte)
            : InstallerCulture.Pick(
                " (no dedicated memory)",
                " (выделенной памяти нет)");
        var uma = string.Empty;
        if (adapter.IsUma)
        {
            uma = adapter.SharedSystemMemoryBytes > 0
                ? string.Format(
                    InstallerCulture.Pick(
                        " [UMA, up to {0:N1} GB shared memory]",
                        " [UMA, до {0:N1} ГБ общей памяти]"),
                    adapter.SharedSystemMemoryBytes / BytesPerGigabyte)
                : " [UMA]";
        }
        var software = adapter.IsSoftware
            ? InstallerCulture.Pick(" [software]", " [программный]")
            : string.Empty;
        return adapter.Name + memory + uma + software;
    }

    /// <summary>
    /// For a sentence that is written to the run log. The log sits beside an English journal
    /// and stays English, so a Russian parenthetical in the middle of it helps nobody.
    /// </summary>
    public static string DescribeInEnglish(GpuAdapterSnapshot adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var memory = adapter.DedicatedLocalBytes > 0
            ? FormattableString.Invariant(
                $" ({adapter.DedicatedLocalBytes / BytesPerGigabyte:N1} GB dedicated)")
            : " (no dedicated memory)";
        var uma = string.Empty;
        if (adapter.IsUma)
        {
            uma = adapter.SharedSystemMemoryBytes > 0
                ? FormattableString.Invariant(
                    $" [UMA, up to {adapter.SharedSystemMemoryBytes / BytesPerGigabyte:N1} GB shared memory]")
                : " [UMA]";
        }

        return adapter.Name + memory + uma + (adapter.IsSoftware ? " [software]" : string.Empty);
    }
}
