using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using Xunit;

namespace LocalAi.IntegrationTests;

public sealed class RuntimeEvidenceReaderTests
{
    [Fact]
    public void Measures_the_component_assembly_file_without_claiming_memory_or_provenance()
    {
        using var fixture = new EvidenceFixture();
        var assembly = typeof(RuntimeEvidenceReader).Assembly;

        var evidence = RuntimeEvidenceReader.Capture(assembly, "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Hash(File.ReadAllBytes(assembly.Location)), evidence.ComponentFileSha256.Value);
        Assert.Equal("observed", evidence.ComponentFileSha256.Basis);
        Assert.Equal("file_bytes_not_memory_or_provenance", evidence.ComponentFileSha256.Reason);
        Assert.Equal("assembly_file", evidence.ImageKind.Value);
        Assert.Equal(assembly.GetName().Version!.ToString(), evidence.AssemblyVersion.Value);
        Assert.Equal("not_evaluated", evidence.ModelSelection.Basis);
        Assert.Equal("not_evaluated", evidence.IndexState.Basis);
        Assert.Equal("unknown", evidence.ReleaseManifestSha256.Basis);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "settings")));
    }

    [Fact]
    public void Does_not_use_the_process_host_for_an_assembly_without_a_file()
    {
        using var fixture = new EvidenceFixture();

        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly(""), "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(evidence.ComponentFileSha256.Value);
        Assert.Equal("unknown", evidence.ComponentFileSha256.Basis);
        Assert.Equal("component_file_unavailable", evidence.ComponentFileSha256.Reason);
        Assert.Equal("unknown", evidence.ImageKind.Basis);
    }

    [Fact]
    public void Missing_and_changed_component_bytes_are_measured_anew_instead_of_using_the_pointer()
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteInstallation();
        var path = Path.Combine(fixture.Root, "fixture.dll");
        var assembly = new EvidenceAssembly(path);
        var missing = RuntimeEvidenceReader.Capture(assembly, "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("missing", missing.ComponentFileSha256.Reason);
        File.WriteAllText(path, "first component bytes");
        var first = RuntimeEvidenceReader.Capture(assembly, "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllText(path, "other component bytes");
        var second = RuntimeEvidenceReader.Capture(assembly, "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Hash(File.ReadAllBytes(path)), second.ComponentFileSha256.Value);
        Assert.NotEqual(first.ComponentFileSha256.Value, second.ComponentFileSha256.Value);
        Assert.Equal("recorded", second.InstalledVersionDirectory.Basis);
        Assert.Equal("recorded", second.InstalledReleaseVersion.Basis);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.3+abcdef1")]
    [InlineData("private-project+not-a-source-commit")]
    public void Partial_or_absent_source_metadata_stays_unknown(string? informationalVersion)
    {
        using var fixture = new EvidenceFixture();
        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly("", informationalVersion), "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(evidence.DeclaredSourceCommit.Value);
        Assert.Equal("unknown", evidence.DeclaredSourceCommit.Basis);
    }

    [Fact]
    public void A_full_declared_source_commit_is_recorded_without_a_provenance_claim()
    {
        using var fixture = new EvidenceFixture();
        var commit = new string('a', 40);
        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly("", "1.2.3+" + commit), "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(commit, evidence.DeclaredSourceCommit.Value);
        Assert.Equal("recorded", evidence.DeclaredSourceCommit.Basis);
        Assert.Equal("assembly_metadata_not_build_provenance", evidence.DeclaredSourceCommit.Reason);
    }

    [Fact]
    public void Installation_fields_require_a_matching_valid_record_and_have_an_observed_record_digest()
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteInstallation();
        var evidence = fixture.Capture();
        Assert.Equal(EvidenceFixture.VersionDirectory, evidence.InstalledVersionDirectory.Value);
        Assert.Equal("1.2.3-local.fixture", evidence.InstalledReleaseVersion.Value);
        Assert.Equal("recorded", evidence.InstalledReleaseVersion.Basis);
        Assert.Equal("pointer_record_agreement_not_binary_verification", evidence.InstalledReleaseVersion.Reason);
        Assert.Equal(Hash(File.ReadAllBytes(fixture.RecordPath)), evidence.InstalledRecordSha256.Value);
        Assert.Equal("observed", evidence.InstalledRecordSha256.Basis);
        File.WriteAllText(fixture.RecordPath, "{\"schemaVersion\":1,\"versionDirectory\":\"bbbbbbbbbbbb\",\"releaseVersion\":\"9.8.7\"}");
        Assert.Equal("pointer_record_mismatch", fixture.Capture().InstalledReleaseVersion.Reason);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"versionDirectory\":\"aaaaaaaaaaaa\",\"releaseVersion\":\"1.2.3\"}")]
    [InlineData("{\"schemaVersion\":1,\"versionDirectory\":\"aaaaaaaaaaaa\",\"releaseVersion\":\"C:/private/project\"}")]
    [InlineData("{\"schemaVersion\":1,\"versionDirectory\":\"aaaaaaaaaaaa\",\"releaseVersion\":\"private-project\"}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"versionDirectory\":\"aaaaaaaaaaaa\",\"releaseVersion\":\"1.2.3\"}")]
    [InlineData("{broken")]
    public void Invalid_installation_records_are_not_reported_as_versions(string json)
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteInstallation();
        File.WriteAllText(fixture.RecordPath, json);
        var evidence = fixture.Capture();
        Assert.Equal("unknown", evidence.InstalledReleaseVersion.Basis);
        Assert.Null(evidence.InstalledReleaseVersion.Value);
    }

    [Fact]
    public void An_invalid_or_oversized_pointer_cannot_make_a_record_look_active()
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteInstallation();
        File.WriteAllText(fixture.PointerPath, "{\"schemaVersion\":1,\"version\":\"../private\"}");
        Assert.Equal("unknown", fixture.Capture().InstalledVersionDirectory.Basis);
        using (var stream = new FileStream(fixture.PointerPath, FileMode.Create)) stream.SetLength(RuntimeEvidenceReader.MaximumMetadataBytes + 1L);
        var evidence = fixture.Capture();
        Assert.Equal("file_size_out_of_bounds", evidence.InstalledVersionDirectory.Reason);
        Assert.Equal("unknown", evidence.InstalledReleaseVersion.Basis);
    }

    [Fact]
    public void Missing_settings_use_the_embedded_catalog_without_creating_directories()
    {
        using var fixture = new EvidenceFixture();
        var absentRoot = Path.Combine(fixture.Root, "not-created");
        var evidence = RuntimeEvidenceReader.Capture(typeof(RuntimeEvidenceReader).Assembly, "fixture", absentRoot, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("embedded", evidence.CatalogSource.Value);
        Assert.Equal(ModelRoutingCatalogStore.LoadEmbedded().CatalogVersion, evidence.CatalogVersion.Value);
        Assert.Equal("observed", evidence.CatalogSha256.Basis);
        Assert.False(Directory.Exists(absentRoot));
    }

    [Fact]
    public void Configured_catalog_uses_the_same_validated_canonical_identity_as_the_effective_reader()
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteCatalog();
        var expected = new ModelRoutingCatalogStore(fixture.Root).ReadWithSource().Document;
        var first = fixture.Capture();
        Assert.Equal("configured", first.CatalogSource.Value);
        Assert.Equal(expected.CatalogVersion, first.CatalogVersion.Value);
        Assert.Equal(expected.CatalogVersion["custom-".Length..], first.CatalogSha256.Value);
        Assert.Equal("canonical_catalog_document_not_raw_file_digest", first.CatalogSha256.Reason);
        var options = new JsonSerializerOptions(LocalAiJson.Strict) { WriteIndented = true };
        File.WriteAllBytes(fixture.CatalogPath, JsonSerializer.SerializeToUtf8Bytes(ModelRoutingCatalogStore.LoadEmbedded(), options));
        Assert.Equal(first.CatalogSha256.Value, fixture.Capture().CatalogSha256.Value);
    }

    [Fact]
    public void Invalid_or_oversized_configured_catalog_does_not_silently_fall_back_to_embedded()
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteCatalog();
        File.WriteAllText(fixture.CatalogPath, "{broken");
        Assert.Equal("invalid_catalog", fixture.Capture().CatalogSource.Reason);
        using (var stream = new FileStream(fixture.CatalogPath, FileMode.Create)) stream.SetLength(RuntimeEvidenceReader.MaximumCatalogBytes + 1L);
        var evidence = fixture.Capture();
        Assert.Equal("file_size_out_of_bounds", evidence.CatalogSource.Reason);
        Assert.Equal("unknown", evidence.CatalogVersion.Basis);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("record")]
    [InlineData("component")]
    public void Linked_files_are_not_followed(string kind)
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteInstallation();
        fixture.WriteCatalog();
        var path = kind == "catalog" ? fixture.CatalogPath : kind == "record" ? fixture.RecordPath : Path.Combine(fixture.Root, "component.dll");
        var target = Path.Combine(fixture.Root, "link-target.txt");
        File.WriteAllText(target, "private link target bytes");
        if (File.Exists(path)) File.Delete(path);
        try { File.CreateSymbolicLink(path, target); }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("This host cannot create the symbolic link required by this test.");
            return;
        }
        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly(kind == "component" ? path : ""), "fixture", fixture.Root, cancellationToken: TestContext.Current.CancellationToken);
        var field = kind == "catalog" ? evidence.CatalogSource : kind == "record" ? evidence.InstalledRecordSha256 : evidence.ComponentFileSha256;
        Assert.Equal("linked_path", field.Reason);
        Assert.Equal("unknown", field.Basis);
    }

    [Fact]
    public void Unc_roots_and_large_component_files_are_rejected_without_guessing_values()
    {
        using var fixture = new EvidenceFixture();
        var path = Path.Combine(fixture.Root, "large.dll");
        using (var stream = new FileStream(path, FileMode.Create)) stream.SetLength(RuntimeEvidenceReader.MaximumComponentBytes + 1);
        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly(path), "fixture", "\\\\invalid-host\\private-share", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("file_size_out_of_bounds", evidence.ComponentFileSha256.Reason);
        Assert.Equal("unsafe_runtime_path", evidence.CatalogSource.Reason);
        Assert.Equal("unknown", evidence.InstalledReleaseVersion.Basis);
    }

    [Fact]
    public void A_linked_settings_ancestor_does_not_become_an_embedded_catalog_fallback()
    {
        using var fixture = new EvidenceFixture();
        var target = Path.Combine(fixture.Root, "private-settings-target");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "settings"), target); }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("This host cannot create the symbolic link required by this test.");
            return;
        }
        var evidence = fixture.Capture();
        Assert.Equal("linked_path", evidence.CatalogSource.Reason);
        Assert.Equal("unknown", evidence.CatalogSource.Basis);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void Index_evidence_is_bounded_opaque_and_does_not_infer_a_source_commit()
    {
        using var fixture = new EvidenceFixture();
        var index = new RuntimeIndexEvidence(1, new string('a', 64), new string('b', 64), null, new string('c', 64));
        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly(""), "fixture", fixture.Root, [index], TestContext.Current.CancellationToken);
        Assert.Equal(index, Assert.Single(evidence.Indexes));
        Assert.Null(evidence.Indexes[0].SourceCommit);
        Assert.Equal("recorded", evidence.IndexState.Basis);
        var invalid = RuntimeEvidenceReader.Capture(new EvidenceAssembly(""), "fixture", fixture.Root,
            [index with { ManifestSha256 = "C:/private/repository" }], TestContext.Current.CancellationToken);
        var unknown = Assert.Single(invalid.Indexes);
        Assert.Equal(1, unknown.Ordinal);
        Assert.Equal("unknown", unknown.Basis);
        Assert.Null(unknown.ManifestSha256);
        Assert.Equal("index_evidence_unknown", invalid.IndexState.Reason);
        var many = Enumerable.Range(1, RuntimeEvidenceReader.MaximumIndexes + 1).Select(ordinal => index with { Ordinal = ordinal }).ToArray();
        Assert.Equal("index_evidence_limit", RuntimeEvidenceReader.Capture(new EvidenceAssembly(""), "fixture", fixture.Root, many, TestContext.Current.CancellationToken).IndexState.Reason);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("recorded")]
    public void Unknown_or_invalid_index_slots_preserve_healthy_siblings_without_upgrading_the_snapshot(string middleBasis)
    {
        using var fixture = new EvidenceFixture();
        var first = new RuntimeIndexEvidence(1, new string('a', 64), new string('b', 64), null, new string('c', 64));
        var middle = new RuntimeIndexEvidence(2,
            middleBasis == "unknown" ? new string('d', 64) : "C:/private/repository",
            new string('e', 64), new string('f', 40), new string('a', 64), middleBasis);
        var last = first with { Ordinal = 3, GenerationId = new string('d', 64) };

        var evidence = RuntimeEvidenceReader.Capture(new EvidenceAssembly(""), "fixture", fixture.Root,
            [first, middle, last], TestContext.Current.CancellationToken);

        Assert.Equal(3, evidence.Indexes.Count);
        Assert.Equal(first, evidence.Indexes[0]);
        Assert.Equal(last, evidence.Indexes[2]);
        var unknown = evidence.Indexes[1];
        Assert.Equal(2, unknown.Ordinal);
        Assert.Equal("unknown", unknown.Basis);
        Assert.Null(unknown.ManifestSha256);
        Assert.Null(unknown.GenerationId);
        Assert.Null(unknown.SourceCommit);
        Assert.Null(unknown.EmbeddingModelSha256);
        Assert.Equal("unknown", evidence.IndexState.Basis);
        Assert.Null(evidence.IndexState.Value);
        Assert.Equal("partial_index_evidence", evidence.IndexState.Reason);
        Assert.DoesNotContain("private/repository", RuntimeEvidenceReader.ToJson(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_is_shared_camel_case_and_contains_no_paths_or_catalog_model_names()
    {
        using var fixture = new EvidenceFixture();
        fixture.WriteInstallation();
        fixture.WriteCatalog();
        var evidence = fixture.Capture();
        var json = RuntimeEvidenceReader.ToJson(evidence);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("localai.runtime-evidence.v1", document.RootElement.GetProperty("schema").GetString());
        Assert.True(document.RootElement.TryGetProperty("componentFileSha256", out _));
        Assert.False(document.RootElement.TryGetProperty("ComponentFileSha256", out _));
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-share", json, StringComparison.OrdinalIgnoreCase);
        foreach (var model in ModelRoutingCatalogStore.LoadEmbedded().Models)
            Assert.DoesNotContain(model.Tag, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_is_honored_without_starting_inference_or_creating_runtime_state()
    {
        using var fixture = new EvidenceFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => RuntimeEvidenceReader.Capture(typeof(RuntimeEvidenceReader).Assembly, "fixture", fixture.Root, cancellationToken: stop.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class EvidenceAssembly(string location, string? informationVersion = null) : Assembly
    {
        public override string Location => location;
        public override bool IsDynamic => false;
        public override AssemblyName GetName() => new("EvidenceFixture") { Version = new Version(1, 2, 3, 4) };
        public override AssemblyName GetName(bool copiedName) => GetName();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
            attributeType == typeof(AssemblyInformationalVersionAttribute) && informationVersion is not null
                ? [new AssemblyInformationalVersionAttribute(informationVersion)] : [];
    }

    private sealed class EvidenceFixture : IDisposable
    {
        public const string VersionDirectory = "aaaaaaaaaaaa";
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("localai-runtime-evidence-tests-");
        public string Root => directory.FullName;
        public string PointerPath => Path.Combine(Root, "bin", "current.json");
        public string RecordPath => Path.Combine(Root, "bin", "installed-release.json");
        public string CatalogPath => Path.Combine(Root, "settings", ModelRoutingCatalogStore.FileName);
        public RuntimeEvidenceSnapshot Capture() => RuntimeEvidenceReader.Capture(typeof(RuntimeEvidenceReader).Assembly, "fixture", Root, cancellationToken: TestContext.Current.CancellationToken);

        public void WriteInstallation()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PointerPath)!);
            File.WriteAllText(PointerPath, "{\"schemaVersion\":1,\"version\":\"aaaaaaaaaaaa\"}");
            File.WriteAllText(RecordPath, "{\"schemaVersion\":1,\"versionDirectory\":\"aaaaaaaaaaaa\",\"releaseVersion\":\"1.2.3-local.fixture\"}");
        }

        public void WriteCatalog()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            File.WriteAllBytes(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(ModelRoutingCatalogStore.LoadEmbedded(), LocalAiJson.Strict));
        }

        public void Dispose()
        {
            var full = Path.GetFullPath(directory.FullName);
            if (!Path.IsPathFullyQualified(full) || !directory.Name.StartsWith("localai-runtime-evidence-tests-", StringComparison.Ordinal) ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup of a substituted fixture root.");
            directory.Delete(recursive: true);
        }
    }
}
