using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The package is downloaded and unpacked into a directory of the run's own — about 800 MB of
/// it — and a window closed mid-update ends the process before any cleanup runs. The next start
/// removes what such a run left, and only that: a directory carrying the installer's marker
/// whose run has ended.
/// </summary>
public sealed class InstallerScratchDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Scratch.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_run_takes_its_download_with_it()
    {
        var scratch = InstallerScratchDirectory.Claim(root);
        File.WriteAllText(Path.Combine(scratch.Path, "localai-package.zip"), "package");

        scratch.Dispose();

        Assert.False(Directory.Exists(scratch.Path));
    }

    [Fact]
    public void What_an_ended_run_left_is_removed_at_the_next_start()
    {
        var scratch = InstallerScratchDirectory.Claim(root);
        var package = Path.Combine(scratch.Path, "localai-package.zip");
        // Held the way an unpacked file is while the process that unpacked it is going away:
        // the run's own cleanup cannot finish, and what it leaves is what a killed run leaves.
        using (var held = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            scratch.Dispose();
        }

        Assert.True(Directory.Exists(scratch.Path));

        InstallerScratchDirectory.RemoveLeftovers(root);

        Assert.False(Directory.Exists(scratch.Path));
    }

    /// <summary>Another installer window may be halfway through its own download.</summary>
    [Fact]
    public void A_run_still_going_keeps_its_directory()
    {
        using var scratch = InstallerScratchDirectory.Claim(root);
        File.WriteAllText(Path.Combine(scratch.Path, "localai-package.zip"), "package");

        InstallerScratchDirectory.RemoveLeftovers(root);

        Assert.True(File.Exists(Path.Combine(scratch.Path, "localai-package.zip")));
    }

    [Fact]
    public void A_directory_without_the_installers_marker_is_left_alone()
    {
        var unmarked = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        var earlier = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unmarked);
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(unmarked, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(earlier, "localai.exe"), "binary");

        InstallerScratchDirectory.RemoveLeftovers(root);

        Assert.True(File.Exists(Path.Combine(unmarked, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(earlier, "localai.exe")));
    }

    [Fact]
    public void A_start_with_nothing_left_behind_is_quiet()
    {
        InstallerScratchDirectory.RemoveLeftovers(Path.Combine(root, "never-created"));
    }
}
