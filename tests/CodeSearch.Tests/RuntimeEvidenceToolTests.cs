using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Mcp;

namespace CodeSearch.Tests;

public sealed class RuntimeEvidenceToolTests
{
    [Fact]
    public void Identity_only_status_identifies_this_mcp_without_repository_services()
    {
        // Null dependencies prove the opt-in branch does not inspect or warm an index.
        var response = CodeSearchTools.IndexStatus(null!, null!, runtimeEvidenceOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(response);
        var identity = json.RootElement;
        Assert.Equal("localai.runtime-evidence.v1", identity.GetProperty("schema").GetString());
        Assert.Equal("codesearch-mcp", identity.GetProperty("component").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(CodeSearchTools).Assembly.Location))),
            identity.GetProperty("componentFileSha256").GetProperty("value").GetString());
        Assert.Equal("not_evaluated", identity.GetProperty("indexState").GetProperty("basis").GetString());
        Assert.Equal("not_evaluated", identity.GetProperty("modelSelection").GetProperty("basis").GetString());
    }
}
