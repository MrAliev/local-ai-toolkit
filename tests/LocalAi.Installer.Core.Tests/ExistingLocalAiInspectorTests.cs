using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Tests;

public sealed class ExistingLocalAiInspectorTests : IDisposable
{
    // Derived from the layout contract so the fixture cannot drift from the real package.
    private static readonly string[] RequiredFiles =
        [.. LocalAiPackageLayout.VersionRequiredFiles];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Classifies_missing_current_pointer_as_absent()
    {
        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Absent, snapshot.State);
    }

    /// <summary>
    /// What the removal stopped after bin left behind (#614): the runtime's own files, names the
    /// installer never wrote, and the keys. Nothing is installed, but it is not nothing either.
    /// </summary>
    [Fact]
    public void A_root_left_without_a_pointer_is_leftovers()
    {
        foreach (var directory in new[] { "settings", "jobs", "telemetry", "seed-unknown-dir", "release-signing" })
        {
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, directory));
        }

        foreach (var file in new[] { "retention.json", "diagnostics.jsonl", "update-state.json", "seed-файл с пробелом.txt" })
        {
            File.WriteAllText(Path.Combine(RuntimeRoot, file), "{}");
        }

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Leftovers, snapshot.State);
        Assert.Null(snapshot.Version);
        Assert.Null(snapshot.Reason);
        Assert.False(snapshot.BinWithoutPointer);
    }

    /// <summary>
    /// A removal whose deletion of bin failed partway can leave bin without its pointer, and an
    /// installation refuses to adopt a bin it did not create: that is told apart from the rest
    /// of what is left.
    /// </summary>
    [Fact]
    public void A_bin_left_without_its_pointer_is_told_apart()
    {
        Directory.CreateDirectory(Path.Combine(BinRoot, "launcher"));
        Directory.CreateDirectory(Path.Combine(RuntimeRoot, "jobs"));

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Leftovers, snapshot.State);
        Assert.True(snapshot.BinWithoutPointer);
    }

    /// <summary>One entry beside the keys is enough, a bin without its pointer included.</summary>
    [Theory]
    [InlineData("bin", true)]
    [InlineData("retention.json", false)]
    public void Anything_beside_the_signing_keys_is_a_leftover(string name, bool directory)
    {
        Directory.CreateDirectory(Path.Combine(RuntimeRoot, "release-signing"));
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, name));
        }
        else
        {
            File.WriteAllText(Path.Combine(RuntimeRoot, name), "{}");
        }

        Assert.Equal(ExistingLocalAiState.Leftovers, CreateInspector().Inspect(_root).State);
    }

    [Fact]
    public void The_signing_keys_alone_are_what_a_full_removal_keeps()
    {
        Directory.CreateDirectory(Path.Combine(RuntimeRoot, "release-signing"));

        Assert.Equal(ExistingLocalAiState.SigningKeysOnly, CreateInspector().Inspect(_root).State);
    }

    /// <summary>
    /// The PATH step runs after the files, so a removal stopped between them leaves the entry
    /// with nothing, or only the keys, in the root.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_launcher_still_on_the_path_is_a_leftover(bool keysKept)
    {
        if (keysKept)
        {
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "release-signing"));
        }

        userPath = @"C:\Tools;" + Path.Combine(RuntimeRoot, "bin", "launcher") + @"\";

        Assert.Equal(ExistingLocalAiState.Leftovers, CreateInspector().Inspect(_root).State);
    }

    [Fact]
    public void An_empty_root_and_a_clean_path_are_absent()
    {
        Directory.CreateDirectory(RuntimeRoot);
        userPath = @"C:\Tools";

        Assert.Equal(ExistingLocalAiState.Absent, CreateInspector().Inspect(_root).State);
    }

    [Fact]
    public void A_root_that_cannot_be_listed_is_not_taken_for_the_keys_alone()
    {
        var snapshot = new ExistingLocalAiInspector(new UnlistableFileSystem(), () => null)
            .Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Leftovers, snapshot.State);
    }

    [Fact]
    public void A_path_that_cannot_be_read_names_no_launcher()
    {
        Directory.CreateDirectory(RuntimeRoot);

        var snapshot = new ExistingLocalAiInspector(
                new SystemFileSystemProbe(),
                () => throw new System.Security.SecurityException("denied"))
            .Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Absent, snapshot.State);
    }

    [Fact]
    public void Classifies_complete_strict_pointer_as_compatible()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Compatible, snapshot.State);
        Assert.Equal("v1", snapshot.Version);
        Assert.Equal(Path.Combine(_root, "LocalAi", "bin", "versions", "v1"), snapshot.VersionPath);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"version":}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"version":"v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"v1","unknown":true}""")]
    [InlineData("""{"schemaVersion":2,"version":"v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":".."}""")]
    [InlineData("""{"schemaVersion":1,"version":"sub/v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"sub\\v1"}""")]
    [InlineData("""{"schemaVersion":1,"version":"   "}""")]
    public void Classifies_malformed_unsupported_or_unsafe_pointer_as_unrecognized(string json)
    {
        Directory.CreateDirectory(BinRoot);
        WriteCurrent(json);

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.NotEmpty(snapshot.Reason!);
    }

    /// <summary>
    /// A missing file is what "Update or repair" puts back, as it puts back one that differs;
    /// whether the rest of the directory allows it is decided against the verified release.
    /// </summary>
    [Fact]
    public void Classifies_version_missing_a_required_file_as_compatible()
    {
        CreateComplete("v1");
        File.Delete(Path.Combine(VersionsRoot, "v1", RequiredFiles[0]));
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Compatible, snapshot.State);
        Assert.Equal("v1", snapshot.Version);
        Assert.Equal(Path.Combine(VersionsRoot, "v1"), snapshot.VersionPath);
        Assert.Null(snapshot.Reason);
    }

    /// <summary>A directory where a file belongs is not missing, and no repair removes it.</summary>
    [Fact]
    public void Classifies_directory_in_place_of_a_required_file_as_unrecognized()
    {
        CreateComplete("v1");
        var required = Path.Combine(VersionsRoot, "v1", RequiredFiles[0]);
        File.Delete(required);
        Directory.CreateDirectory(required);
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = CreateInspector().Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Equal("v1", snapshot.Version);
        Assert.Equal(Path.Combine(VersionsRoot, "v1"), snapshot.VersionPath);
        Assert.Equal($"Version 'v1' is missing '{RequiredFiles[0]}'.", snapshot.Reason);
    }

    [Fact]
    public void Classifies_reparse_escape_as_unrecognized()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var versionPath = Path.Combine(VersionsRoot, "v1");
        var fileSystem = new RedirectingPhysicalPathFileSystem(
            versionPath,
            Path.Combine(_root, "outside"));

        var snapshot = new ExistingLocalAiInspector(fileSystem).Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("outside", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_versions_root_that_physically_escapes_bin_root()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var fileSystem = new PrefixRedirectingPhysicalPathFileSystem(
            VersionsRoot,
            Path.Combine(_root, "outside", "versions"));

        var snapshot = new ExistingLocalAiInspector(fileSystem).Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("outside", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Revalidates_version_directory_after_required_files_are_checked()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        var versionPath = Path.Combine(VersionsRoot, "v1");
        var fileSystem = new SwappingPhysicalPathFileSystem(
            versionPath,
            versionPath,
            Path.Combine(_root, "outside", "v1"));

        var snapshot = new ExistingLocalAiInspector(fileSystem).Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("changed", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classifies_native_physical_path_failure_as_unrecognized()
    {
        CreateComplete("v1");
        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");

        var snapshot = new ExistingLocalAiInspector(
                new FailingPhysicalPathFileSystem())
            .Inspect(_root);

        Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
        Assert.Contains("physical path failed", snapshot.Reason!);
    }

    [Fact]
    public void Rejects_real_versions_ancestor_link_escape_when_links_are_supported()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "LocalAi.Installer.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BinRoot);
        Directory.CreateDirectory(outsideRoot);
        var outsideVersion = Path.Combine(outsideRoot, "v1");
        Directory.CreateDirectory(outsideVersion);
        foreach (var file in RequiredFiles)
        {
            File.WriteAllText(Path.Combine(outsideVersion, file), file);
        }

        WriteCurrent("""{"schemaVersion":1,"version":"v1"}""");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(VersionsRoot, outsideRoot);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Creating symbolic links requires Windows Developer Mode.");
                return;
            }

            var snapshot = CreateInspector().Inspect(_root);

            Assert.Equal(ExistingLocalAiState.Unrecognized, snapshot.State);
            Assert.Contains("outside", snapshot.Reason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(VersionsRoot))
            {
                Directory.Delete(VersionsRoot);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string RuntimeRoot => Path.Combine(_root, "LocalAi");
    private string BinRoot => Path.Combine(RuntimeRoot, "bin");
    private string VersionsRoot => Path.Combine(BinRoot, "versions");

    private string? userPath;

    private ExistingLocalAiInspector CreateInspector() => new(new SystemFileSystemProbe(), () => userPath);

    private void CreateComplete(string version)
    {
        var versionPath = Path.Combine(VersionsRoot, version);
        Directory.CreateDirectory(versionPath);
        foreach (var file in RequiredFiles)
        {
            File.WriteAllText(Path.Combine(versionPath, file), file);
        }
    }

    private void WriteCurrent(string json)
    {
        Directory.CreateDirectory(BinRoot);
        File.WriteAllText(Path.Combine(BinRoot, "current.json"), json);
    }

    private sealed class RedirectingPhysicalPathFileSystem(
        string redirectedPath,
        string physicalPath) : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public IEnumerable<string> EnumerateEntries(string directory) => _inner.EnumerateEntries(directory);
        public string ResolvePhysicalPath(string path) =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(redirectedPath),
                StringComparison.OrdinalIgnoreCase)
                ? physicalPath
                : _inner.ResolvePhysicalPath(path);
    }

    private sealed class SwappingPhysicalPathFileSystem(
        string swappedPath,
        string firstPhysicalPath,
        string laterPhysicalPath) : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();
        private int _resolveCount;

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public IEnumerable<string> EnumerateEntries(string directory) => _inner.EnumerateEntries(directory);

        public string ResolvePhysicalPath(string path)
        {
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(path),
                    Path.TrimEndingDirectorySeparator(swappedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return _inner.ResolvePhysicalPath(path);
            }

            return Interlocked.Increment(ref _resolveCount) == 1
                ? firstPhysicalPath
                : laterPhysicalPath;
        }
    }

    private sealed class PrefixRedirectingPhysicalPathFileSystem(
        string logicalRoot,
        string physicalRoot) : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public IEnumerable<string> EnumerateEntries(string directory) => _inner.EnumerateEntries(directory);

        public string ResolvePhysicalPath(string path)
        {
            var relative = Path.GetRelativePath(logicalRoot, path);
            return relative == "." ||
                   (!relative.StartsWith("..", StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative))
                ? Path.GetFullPath(Path.Combine(physicalRoot, relative))
                : _inner.ResolvePhysicalPath(path);
        }
    }

    private sealed class UnlistableFileSystem : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => true;
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public string ResolvePhysicalPath(string path) => _inner.ResolvePhysicalPath(path);
        public IEnumerable<string> EnumerateEntries(string directory) =>
            throw new UnauthorizedAccessException("listing denied");
    }

    private sealed class FailingPhysicalPathFileSystem : IFileSystemProbe
    {
        private readonly SystemFileSystemProbe _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public FileMetadataSnapshot GetMetadata(string path) => _inner.GetMetadata(path);
        public IEnumerable<string> EnumerateEntries(string directory) => _inner.EnumerateEntries(directory);
        public string ResolvePhysicalPath(string path) =>
            throw new System.ComponentModel.Win32Exception(
                "physical path failed");
    }
}
