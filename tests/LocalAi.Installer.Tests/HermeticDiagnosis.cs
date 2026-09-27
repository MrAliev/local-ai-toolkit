using System.Runtime.InteropServices;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A supported machine with every prerequisite present and no adapter, for runs that must not
/// probe this one.
/// </summary>
internal static class HermeticDiagnosis
{
    public static EnvironmentDiagnosis Supported(
        DependencySnapshot? git = null,
        NetworkSnapshot? network = null,
        DependencySnapshot? winGet = null,
        DependencySnapshot? gitHubCli = null,
        GpuSnapshot? gpu = null,
        ExistingLocalAiState existingLocalAi = ExistingLocalAiState.Absent,
        DependencySnapshot? npm = null,
        DependencySnapshot? ollama = null)
    {
        static DependencySnapshot Found(string name) =>
            new(name, DependencyState.Detected, @"C:\absent\" + name + ".exe", "1.0", null);

        return new EnvironmentDiagnosis(
            new OperatingSystemSnapshot(
                "Windows",
                new Version(10, 0, 26200),
                Architecture.X64,
                SupportStatus.Supported,
                SupportStatus.Supported),
            new DiskSnapshot(ObservationState.Available, 100L * 1024 * 1024 * 1024, null),
            network ?? new NetworkSnapshot(ObservationState.Available, null),
            winGet ?? Found("winget"),
            git ?? Found("git"),
            gitHubCli ?? Found("gh"),
            Found("gh-auth"),
            ollama ?? Found("ollama"),
            Found("dotnet"),
            Found("node"),
            npm ?? Found("npm"),
            Found("scip-typescript"),
            Found("python"),
            Found("scip-python"),
            gpu ?? new GpuSnapshot(ObservationState.Unavailable, [], "none in this test"),
            new ExistingLocalAiSnapshot(existingLocalAi, null, null, null),
            [],
            []);
    }
}
