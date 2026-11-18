#:project ../../src/LocalAi.Installer.Core/LocalAi.Installer.Core.csproj
#:property PublishAot=false

using LocalAi.Installer.Core.Dependencies;

// Prepare only an explicitly named, isolated npm fixture. Reuse the installer's
// version and full-bundle hash guards rather than copying its patch expressions.
if (!OperatingSystem.IsWindows() || args is not [var suppliedPrefix])
{
    Console.Error.WriteLine("Usage (Windows): dotnet run --file scripts/quality/prepare-scip-python-fixture.cs -- <path-to-scip-python-fixture>");
    return 2;
}

try
{
    var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppliedPrefix));
    if (!string.Equals(Path.GetFileName(prefix), "scip-python-fixture", StringComparison.OrdinalIgnoreCase)
        || (File.GetAttributes(prefix) & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("Use an isolated directory named scip-python-fixture, not a shared npm installation.");
    var bundle = Path.Combine(prefix, "node_modules", "@sourcegraph", "scip-python", "dist", "scip-python.js");
    for (string? current = bundle; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Refusing a linked path in the SCIP Python fixture.");
    var outcome = new ScipPythonWindowsCompatibilityPatch().Apply(
        Path.Combine(prefix, "node_modules"), ScipPythonWindowsCompatibilityPatch.SupportedVersion);
    Console.WriteLine($"scip-python {ScipPythonWindowsCompatibilityPatch.SupportedVersion}: {outcome}");
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"SCIP Python fixture preparation failed: {error.Message}");
    return 1;
}
