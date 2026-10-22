using System.Diagnostics;

namespace LocalAi.Tests.Shared;

internal static class DirectoryLinkFixture
{
    internal static async Task CreateAsync(string path, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        ValidateWindowsPath(path, nameof(path));
        ValidateWindowsPath(target, nameof(target));
        // mklink is a fixed cmd builtin. Only validated, quoted path values enter its command text.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /v:off /c mklink /J \"{path}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The directory-link process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }
        var text = await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Directory-link creation failed ({process.ExitCode}): {text[0]}{text[1]}");
    }

    private static void ValidateWindowsPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0)
            throw new ArgumentException("Windows directory-link paths cannot contain command-interpreted characters.", parameterName);
    }
}
