using System.Diagnostics;

namespace LocalAi.ReleaseSigner.Tests;

public sealed class PdfPublishTests
{
    [Fact]
    public async Task The_release_script_copies_both_native_libraries_and_the_notice_into_the_package_input()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-pdf-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        try
        {
            var source = new DirectoryInfo(AppContext.BaseDirectory);
            while (source is not null && !File.Exists(Path.Combine(source.FullName, "LocalAi.slnx"))) source = source.Parent;
            Assert.NotNull(source);
            File.Copy(Path.Combine(source.FullName, "scripts", "publish-localai-release-win-x64-self-contained.ps1"),
                Path.Combine(root, "scripts", "publish.ps1"));
            File.WriteAllText(Path.Combine(root, "THIRD-PARTY-NOTICES-PDF.txt"), "license fixture");
            // Execute the real artifact-copying script; only expensive build tools are replaced.
            // The publish stub produces the same loose native files as a win-x64 SDK publish.
            var harness = Path.Combine(root, "harness.ps1");
            File.WriteAllText(harness, """
                function global:dotnet {
                    $global:LASTEXITCODE = 0
                    if ($args[0] -ne 'publish') { return }
                    $output = $args[[Array]::IndexOf($args, '-o') + 1]
                    $name = [IO.Path]::GetFileNameWithoutExtension($args[1])
                    $names = @{
                        'CodeSearch.Cli'='codesearch.exe'; 'CodeSearch.Mcp'='codesearch-mcp.exe';
                        'LocalLm.Mcp'='locallm-mcp.exe'; 'LocalAi.Cli'='localai.exe';
                        'LocalAi.Launcher'='localai-launcher.exe'; 'LocalAi.Broker'='LocalAi.Broker.exe';
                        'LocalAi.Installer'='LocalAi.Installer.exe'
                    }
                    [IO.File]::WriteAllText((Join-Path $output $names[$name]), $name)
                    if ($name -in @('LocalAi.Cli', 'LocalLm.Mcp')) {
                        [IO.File]::WriteAllText((Join-Path $output 'pdfium.dll'), 'pdfium native fixture')
                        [IO.File]::WriteAllText((Join-Path $output 'libSkiaSharp.dll'), 'skia native fixture')
                    }
                }
                & (Join-Path $PSScriptRoot 'scripts/publish.ps1') -ReleaseVersion 9.9.9 -VersionDirectory test-pdf
                """);
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-File", harness }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            var artifacts = Path.Combine(root, "publish", "artifacts");
            Assert.Equal("pdfium native fixture", File.ReadAllText(Path.Combine(artifacts, "pdfium.dll")));
            Assert.Equal("skia native fixture", File.ReadAllText(Path.Combine(artifacts, "libSkiaSharp.dll")));
            Assert.Equal("license fixture", File.ReadAllText(Path.Combine(artifacts, "THIRD-PARTY-NOTICES-PDF.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
