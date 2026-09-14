using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class DoctorUsesConfiguredModelCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localai-catalog-doctor-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_user_catalog_is_reported_with_its_path_and_content_identity()
    {
        var path = Write(JsonSerializer.Serialize(ModelRoutingCatalogResource.LoadDocument(), LocalAiJson.Strict));

        var report = DoctorCommand.Inspect(_root);

        var check = Assert.Single(report.Checks, check => check.Name == "model-catalog");
        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains(check.Facts!, fact => fact.Name == "path" && Equals(fact.Value, path));
        var version = Assert.Single(check.Facts!, fact => fact.Name == "catalogVersion");
        Assert.StartsWith("custom-", Assert.IsType<string>(version.Value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":999}")]
    public void An_invalid_user_catalog_is_reported_as_a_failure(string json)
    {
        var path = Write(json);

        var report = DoctorCommand.Inspect(_root);

        var check = Assert.Single(report.Checks, check => check.Name == "model-catalog");
        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Contains(path, check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_user_catalog_reports_the_shipped_version_without_creating_a_file()
    {
        var report = DoctorCommand.Inspect(_root);

        var check = Assert.Single(report.Checks, check => check.Name == "model-catalog");
        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains(check.Facts!, fact => fact.Name == "catalogVersion" && Equals(fact.Value, "1"));
        Assert.False(Directory.Exists(_root));
    }

    private string Write(string content)
    {
        var path = RuntimeDirectories.SettingsFileForWriting(_root, "model-routing.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
