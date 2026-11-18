using System.Security.Cryptography;
using System.Text.Json;
using LocalLm.Mcp;

namespace LocalLm.Tests;

public sealed class RuntimeEvidenceToolTests
{
    [Fact]
    public async Task Identity_only_status_identifies_this_mcp_without_model_tasks()
    {
        // No ModelManagementTasks exists: querying a model or broker would fail this test.
        var response = await LocalLmTools.LocalModelsStatus(null!, TestContext.Current.CancellationToken, runtimeEvidenceOnly: true);
        using var json = JsonDocument.Parse(response);
        var identity = json.RootElement;
        Assert.Equal("localai.runtime-evidence.v1", identity.GetProperty("schema").GetString());
        Assert.Equal("locallm-mcp", identity.GetProperty("component").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(LocalLmTools).Assembly.Location))),
            identity.GetProperty("componentFileSha256").GetProperty("value").GetString());
        Assert.Equal("not_evaluated", identity.GetProperty("modelSelection").GetProperty("basis").GetString());
    }
}
