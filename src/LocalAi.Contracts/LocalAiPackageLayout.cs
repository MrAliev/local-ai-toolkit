namespace LocalAi.Contracts;

public static class LocalAiPackageLayout
{
    public const string StableLauncherFile = "localai-launcher.exe";

    public const string BrokerFile = "LocalAi.Broker.exe";

    /// <summary>
    /// Every component ships as a self-contained executable, so an installed machine never
    /// needs a system-wide .NET runtime. This minimum also recognizes versions shipped before
    /// PDF support; their launch and rollback must not depend on files they never carried.
    /// </summary>
    public static IReadOnlyList<string> VersionRequiredFiles { get; } =
        Array.AsReadOnly(
        [
            "localai.exe",
            "codesearch.exe",
            "codesearch-mcp.exe",
            "locallm-mcp.exe",
            BrokerFile,
        ]);

    // Compatibility name used by runtime consumers. Package-only artifacts
    // must never be added here because version directories are immutable.
    public static IReadOnlyList<string> RequiredFiles => VersionRequiredFiles;

    public static IReadOnlyList<string> PackageArtifactFiles { get; } =
        Array.AsReadOnly(
            VersionRequiredFiles.Append(StableLauncherFile).ToArray());

    public static IReadOnlyList<string> PdfRuntimeFiles { get; } = Array.AsReadOnly<string>(
        ["pdfium.dll", "libSkiaSharp.dll", "THIRD-PARTY-NOTICES-PDF.txt"]);

    public static IReadOnlyList<string> CurrentVersionFiles { get; } = Array.AsReadOnly(
        VersionRequiredFiles.Concat(PdfRuntimeFiles).Concat(["libHarfBuzzSharp.dll", "THIRD-PARTY-NOTICES-DOCUMENTATION.txt"]).ToArray());

    public static IReadOnlyList<string> CurrentPackageArtifactFiles { get; } = Array.AsReadOnly(
        CurrentVersionFiles.Append(StableLauncherFile).ToArray());

    // Three complete, exact shapes. A partial payload, foreign DLL, or stray notice is never
    // accepted by loosening the package allowlist to arbitrary optional files.
    public static bool IsKnownVersionFiles(IEnumerable<string> files)
    {
        var names = files.ToHashSet(StringComparer.Ordinal);
        return names.SetEquals(VersionRequiredFiles) || names.SetEquals(VersionRequiredFiles.Concat(PdfRuntimeFiles)) || names.SetEquals(CurrentVersionFiles);
    }

    public static bool IsKnownPackageArtifacts(IEnumerable<string> files)
    {
        var names = files.ToHashSet(StringComparer.Ordinal);
        return names.SetEquals(PackageArtifactFiles) || names.SetEquals(PackageArtifactFiles.Concat(PdfRuntimeFiles)) || names.SetEquals(CurrentPackageArtifactFiles);
    }
}
