using System.Runtime.InteropServices;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A supported machine with every prerequisite present and no adapter, for runs that must not
/// probe this one.
/// </summary>
internal static class HermeticDiagnosis
{
    public static EnvironmentDiagnosis Supported()
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
            new NetworkSnapshot(ObservationState.Available, null),
            Found("winget"),
            Found("git"),
            Found("gh"),
            Found("gh-auth"),
            Found("ollama"),
            Found("dotnet"),
            Found("node"),
            Found("npm"),
            Found("scip-typescript"),
            Found("python"),
            Found("scip-python"),
            new GpuSnapshot(ObservationState.Unavailable, [], "none in this test"),
            new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null),
            [],
            []);
    }
}
