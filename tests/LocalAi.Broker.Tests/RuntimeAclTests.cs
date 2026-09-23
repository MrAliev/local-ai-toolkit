using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using LocalAi.Broker;
using LocalAi.Contracts;

#pragma warning disable CA1416

namespace LocalAi.Broker.Tests;

public sealed class RuntimeAclTests
{
    [Theory]
    [InlineData("after-repair")]
    [InlineData("after-initial-read")]
    public void Ensure_holds_the_same_file_until_acl_validation_finishes(string boundary)
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var host = Path.Combine(root.Path, "host.json");
        File.WriteAllText(host, "original");
        if (boundary == "after-initial-read") new RuntimeAcl().Ensure(root.Path);
        var attempted = false;
        var blocked = false;
        void TryPublish()
        {
            attempted = true;
            try { PublishReplacement(host); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { blocked = true; }
        }
        var acl = new RuntimeAcl(
            applyExactAcl: (path, directory, create, user, administrators) =>
            {
                ApplyRealAcl(path, directory, create, user, administrators);
                if (path == host && boundary == "after-repair") TryPublish();
            },
            readAclSnapshot: path =>
            {
                var snapshot = ReadRealAclSnapshot(path);
                if (path == host && boundary == "after-initial-read" && !attempted) TryPublish();
                return snapshot;
            });

        acl.Ensure(root.Path);

        Assert.True(attempted); Assert.True(blocked);
        Assert.Equal("original", File.ReadAllText(host));
        Assert.True(ReadRealAclSnapshot(host).AreAccessRulesProtected);
        PublishReplacement(host);
        Assert.Equal("replacement", File.ReadAllText(host));
    }

    [Fact]
    public void Ensure_does_not_require_sharing_the_contents_of_an_open_file()
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var file = Path.Combine(root.Path, "host.json");
        using var writer = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        new RuntimeAcl().Ensure(root.Path);

        Assert.True(ReadRealAclSnapshot(file).AreAccessRulesProtected);
        writer.WriteByte(42);
    }

    [Fact]
    public void Ensure_can_repair_a_file_without_permission_to_read_its_contents()
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var file = Path.Combine(root.Path, "host.json");
        File.WriteAllText(file, "original");
        var security = new FileInfo(file).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData | FileSystemRights.WriteData, AccessControlType.Deny));
        new FileInfo(file).SetAccessControl(security);
        Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(file));

        new RuntimeAcl().Ensure(root.Path);

        Assert.Equal("original", File.ReadAllText(file));
        Assert.True(ReadRealAclSnapshot(file).AreAccessRulesProtected);
    }

    private static void PublishReplacement(string path) => AtomicFile.Write(path, stream =>
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write("replacement");
    }, AtomicWrite.Recomputable);

    [Fact]
    public void Host_protection_does_not_pin_queue_files_during_directory_publication()
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var source = Path.Combine(root.Path, "staging", "pending");
        var destination = Path.Combine(root.Path, "jobs", "published");
        Directory.CreateDirectory(source);
        var request = Path.Combine(source, "request.json");
        File.WriteAllText(request, "request");
        var moved = false;
        var acl = new RuntimeAcl(applyExactAcl: (path, directory, create, user, administrators) =>
        {
            ApplyRealAcl(path, directory, create, user, administrators);
            if (path == request) { Directory.Move(source, destination); moved = true; }
        });

        acl.Ensure(root.Path);

        Assert.True(moved);
        Assert.Equal("request", File.ReadAllText(Path.Combine(destination, "request.json")));
    }

    [Fact]
    public void Ensure_rechecks_a_secured_tree_without_spending_any_acl_write_operations()
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var file = Path.Combine(root.Path, "jobs", "request.json");
        File.WriteAllText(file, "{}");
        new RuntimeAcl().Ensure(root.Path);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acl = new RuntimeAcl(
            applyExactAcl: (_, _, _, _, _) => throw new InvalidOperationException("ACL write budget exceeded"),
            readAclSnapshot: path =>
            {
                read.Add(path);
                return ReadRealAclSnapshot(path);
            });

        acl.Ensure(root.Path);

        var expected = Directory.EnumerateFileSystemEntries(root.Path, "*", SearchOption.AllDirectories)
            .Append(root.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(read), "Every existing path must still have its real ACL checked.");
        Assert.Equal("{}", File.ReadAllText(file));
    }

    [Fact]
    public void Ensure_repairs_a_changed_child_without_rewriting_unchanged_siblings()
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var changed = Path.Combine(root.Path, "jobs", "changed.json");
        var unchanged = Path.Combine(root.Path, "jobs", "unchanged.json");
        File.WriteAllText(changed, "changed");
        File.WriteAllText(unchanged, "unchanged");
        var writes = new List<string>();
        var acl = new RuntimeAcl(
            applyExactAcl: (path, directory, create, user, administrators) =>
            {
                writes.Add(path);
                ApplyRealAcl(path, directory, create, user, administrators);
            },
            readAclSnapshot: ReadRealAclSnapshot);
        acl.Ensure(root.Path);
        var security = new FileInfo(changed).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),
            FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(changed).SetAccessControl(security);
        writes.Clear();

        acl.Ensure(root.Path);

        Assert.Equal([changed], writes);
        Assert.DoesNotContain(ReadRealAclSnapshot(changed).Entries, entry => entry.SecurityIdentifier == "S-1-1-0");
        Assert.Equal("changed", File.ReadAllText(changed));
        Assert.Equal("unchanged", File.ReadAllText(unchanged));
    }

    [Fact]
    public void Ensure_attempts_repair_when_the_initial_acl_cannot_be_read_and_validates_afterwards()
    {
        using var root = new TemporaryRuntimeRoot();
        new RuntimeAcl().Ensure(root.Path);
        var unreadable = true;
        var repaired = false;
        var verified = false;
        var acl = new RuntimeAcl(
            applyExactAcl: (path, directory, create, user, administrators) =>
            {
                Assert.Equal(root.Path, path);
                ApplyRealAcl(path, directory, create, user, administrators);
                repaired = true;
            },
            readAclSnapshot: path =>
            {
                if (path == root.Path && unreadable)
                {
                    unreadable = false;
                    throw new UnauthorizedAccessException("Initial DACL read denied");
                }
                if (path == root.Path) verified = repaired;
                return ReadRealAclSnapshot(path);
            });

        acl.Ensure(root.Path);

        Assert.True(verified);
    }

    [Fact]
    public void Ensure_ignores_a_job_directory_moved_during_acl_application()
    {
        using var root = new TemporaryRuntimeRoot();
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            Directory.CreateDirectory(Path.Combine(root.Path, child));
        }

        var source = Path.Combine(root.Path, "jobs", "job-1");
        var destination = Path.Combine(root.Path, "archive", "job-1");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "request.json"), "{}");
        var moved = false;
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: userSid,
            applyExactAcl: (path, isDirectory, createNew, user, administrators) =>
            {
                if (!moved &&
                    string.Equals(path, source, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(source, destination);
                    moved = true;
                    throw new InvalidOperationException(
                        "Method failed with unexpected error code 3.");
                }

                ApplyRealAcl(path, isDirectory, createNew, user, administrators);
            },
            readAclSnapshot: ReadRealAclSnapshot);

        acl.Ensure(root.Path);

        Assert.True(moved);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void Ensure_propagates_acl_failure_while_the_target_still_exists()
    {
        using var root = new TemporaryRuntimeRoot();
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            Directory.CreateDirectory(Path.Combine(root.Path, child));
        }

        var target = Path.Combine(root.Path, "jobs", "job-1");
        Directory.CreateDirectory(target);
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: userSid,
            applyExactAcl: (path, isDirectory, createNew, user, administrators) =>
            {
                if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ACL write failed.");
                }

                ApplyRealAcl(path, isDirectory, createNew, user, administrators);
            },
            readAclSnapshot: ReadRealAclSnapshot);

        var error = Assert.Throws<InvalidOperationException>(
            () => acl.Ensure(root.Path));

        Assert.Equal("ACL write failed.", error.Message);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Ensure_applies_and_validates_each_path_in_the_same_pass()
    {
        using var root = new TemporaryRuntimeRoot();
        // Pre-create the standard subdirectories so EnsureDirectory() (which only applies an ACL
        // to directories it creates itself) is a no-op here, and every apply/validate call
        // recorded below comes from the single unified walk this test is actually about.
        foreach (var child in new[] { "jobs", "archive", "staging", "quarantine" })
        {
            Directory.CreateDirectory(Path.Combine(root.Path, child));
        }

        File.WriteAllText(Path.Combine(root.Path, "jobs", "placeholder.txt"), "x");

        var callOrder = new List<string>();
        var userSid = WindowsIdentity.GetCurrent().User!.Value;

        var acl = new RuntimeAcl(
            isWindows: true,
            currentUser: userSid,
            applyExactAcl: (path, isDirectory, createNew, user, administrators) =>
            {
                callOrder.Add("apply:" + path);
                ApplyRealAcl(path, isDirectory, createNew, user, administrators);
            },
            readAclSnapshot: path =>
            {
                callOrder.Add("validate:" + path);
                return ReadRealAclSnapshot(path);
            });

        // Initial read-only checks are allowed. Every repaired path is immediately re-validated before moving on,
        // so a path created by someone else after this call finishes can never be caught
        // mid-way between an "apply everywhere" pass and a separate "validate everywhere" pass.
        acl.Ensure(root.Path);

        Assert.Contains(callOrder, item => item.StartsWith("apply:", StringComparison.Ordinal));
        for (var i = 0; i < callOrder.Count; i++)
        {
            if (!callOrder[i].StartsWith("apply:", StringComparison.Ordinal)) continue;
            var path = callOrder[i]["apply:".Length..];
            Assert.True(i + 1 < callOrder.Count);
            Assert.Equal(
                "validate:" + path,
                callOrder[i + 1]);
        }
    }

    [Fact]
    public void Ensure_on_real_filesystem_does_not_throw_for_a_freshly_created_root()
    {
        using var root = new TemporaryRuntimeRoot();

        new RuntimeAcl().Ensure(root.Path);
    }

    [Fact]
    public void Ensure_is_idempotent_when_called_twice_in_a_row()
    {
        using var root = new TemporaryRuntimeRoot();
        var acl = new RuntimeAcl();

        acl.Ensure(root.Path);
        acl.Ensure(root.Path);
    }

    /// <summary>
    /// The blast-radius half of #200: a junction inside the runtime tree — creatable by the
    /// same unprivileged user who owns the root — used to lead the ACL repair outside it.
    /// The walk must refuse the reparse point by name and never touch what it points at.
    /// </summary>
    [Fact]
    public void Ensure_refuses_a_junction_and_leaves_its_target_untouched()
    {
        using var root = new TemporaryRuntimeRoot();
        using var outside = new TemporaryRuntimeRoot();
        File.WriteAllText(Path.Combine(outside.Path, "sentinel.txt"), "alive");
        Directory.CreateDirectory(Path.Combine(root.Path, "jobs"));
        var junction = Path.Combine(root.Path, "jobs", "detour");
        CreateJunction(junction, outside.Path);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new RuntimeAcl().Ensure(root.Path));

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outside.Path, "sentinel.txt")));
            // The external tree keeps its ordinary inherited ACL: the exact-ACL repair,
            // which always protects what it touches, never followed the junction.
            Assert.False(
                new DirectoryInfo(outside.Path).GetAccessControl().AreAccessRulesProtected);
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    /// <summary>
    /// The cycle half of #200. NTFS cannot form a directory cycle without a reparse point,
    /// so refusing every reparse point is also what keeps this walk finite.
    /// </summary>
    [Fact]
    public void Ensure_refuses_a_junction_cycle()
    {
        using var root = new TemporaryRuntimeRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "jobs"));
        var junction = Path.Combine(root.Path, "jobs", "loop");
        CreateJunction(junction, root.Path);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => new RuntimeAcl().Ensure(root.Path));

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // cmd's mklink /J: junction creation needs no privilege, unlike symlinks, which is
        // exactly why the walk has to defend against it.
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0 && Directory.Exists(link),
            $"Could not create a junction at '{link}': " + process.StandardError.ReadToEnd());
    }

    /// <summary>
    /// Removes the link itself, never its target. cmd's rmdir deletes a junction without
    /// recursing into it, which managed recursive deletion refuses to do here.
    /// </summary>
    private static void RemoveJunction(string link)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("rmdir");
        start.ArgumentList.Add(link);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.False(
            Directory.Exists(link),
            $"Could not remove the junction at '{link}': " +
            process.StandardError.ReadToEnd());
    }

    private static void ApplyRealAcl(
        string path,
        bool isDirectory,
        bool createNew,
        string currentUserSid,
        string administratorsSid)
    {
        var user = new SecurityIdentifier(currentUserSid);
        var administrators = new SecurityIdentifier(administratorsSid);
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            var directory = new DirectoryInfo(path);
            if (createNew)
            {
                directory.Create(security);
            }
            else
            {
                directory.SetAccessControl(security);
            }

            return;
        }

        if (createNew)
        {
            throw new InvalidOperationException("Runtime ACL cannot create a file node.");
        }

        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fileSecurity);
    }

    private static RuntimeAclSnapshot ReadRealAclSnapshot(string path)
    {
        var isDirectory = Directory.Exists(path);
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var entries = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new RuntimeAclEntry(
                rule.IdentityReference.Value,
                rule.FileSystemRights,
                rule.AccessControlType,
                rule.InheritanceFlags,
                rule.PropagationFlags,
                rule.IsInherited))
            .ToArray();
        return new RuntimeAclSnapshot(
            isDirectory,
            security.AreAccessRulesProtected,
            entries);
    }
}
