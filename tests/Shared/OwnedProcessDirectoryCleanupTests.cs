namespace LocalAi.Tests.Shared;

public sealed class OwnedProcessDirectoryCleanupTests
{
    [Fact]
    public void A_mapped_image_is_deleted_after_its_owner_unmaps_it()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows mapped executable fixture.");
        var root = NewRoot();
        var path = Path.Combine(root, "localai-launcher.exe~RF-owned.TMP");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        var module = System.Runtime.InteropServices.NativeLibrary.Load(path);
        Exception? observed = null;
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                OwnedProcessDirectoryCleanup.Delete(root, maximumAttempts: 1));
            OwnedProcessDirectoryCleanup.Delete(root, (exception, _) =>
            {
                observed = exception;
                if (module != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.NativeLibrary.Free(module);
                    module = IntPtr.Zero;
                }
            }, delay: _ => { });
            Assert.NotNull(observed);
            Assert.Equal(5, observed.HResult & 0xffff);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (module != IntPtr.Zero) System.Runtime.InteropServices.NativeLibrary.Free(module);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_locked_payload_is_deleted_after_its_owner_releases_it()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows file sharing fixture.");
        var root = NewRoot();
        var path = Path.Combine(root, "LocalAi.Broker.Core.dll");
        File.WriteAllText(path, "owned fixture payload");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Exception? observed = null;
        try
        {
            OwnedProcessDirectoryCleanup.Delete(root, (exception, _) =>
            {
                observed = exception;
                held.Dispose();
            }, delay: _ => { });
            Assert.NotNull(observed);
            Assert.True(observed is IOException or UnauthorizedAccessException);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            held.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_permanent_lock_still_fails_at_the_attempt_limit()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows file sharing fixture.");
        var root = NewRoot();
        var path = Path.Combine(root, "localai-launcher.exe~RF-owned.TMP");
        File.WriteAllText(path, "owned retired image fixture");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var retries = 0;
        try
        {
            var failure = Record.Exception(() => OwnedProcessDirectoryCleanup.Delete(root,
                (_, _) => retries++, maximumAttempts: 2, delay: _ => { }));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(1, retries);
            Assert.True(File.Exists(path));
        }
        finally
        {
            held.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_only_is_preserved_and_the_failure_is_not_suppressed()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows read-only fixture.");
        var root = NewRoot();
        var path = Path.Combine(root, "LocalAi.Broker.exe");
        File.WriteAllText(path, "owned read-only fixture");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => OwnedProcessDirectoryCleanup.Delete(root,
                maximumAttempts: 2, delay: _ => { }));
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_temp_root_itself_and_paths_outside_it_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => OwnedProcessDirectoryCleanup.Delete(Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => OwnedProcessDirectoryCleanup.Delete(
            Path.GetPathRoot(Path.GetTempPath())!));
        Assert.Throws<ArgumentException>(() => OwnedProcessDirectoryCleanup.Delete(
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + "-outside-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void A_file_at_the_fixture_path_is_not_silently_treated_as_absent()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "not-a-directory");
        File.WriteAllText(path, "owned fixture");
        try
        {
            Assert.ThrowsAny<IOException>(() => OwnedProcessDirectoryCleanup.Delete(path,
                (_, _) => Assert.Fail("A non-directory path must not retry.")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_absent_fixture_is_already_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-owned-cleanup-" + Guid.NewGuid().ToString("N"));
        OwnedProcessDirectoryCleanup.Delete(root, (_, _) => Assert.Fail("An absent fixture must not retry."));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-owned-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
