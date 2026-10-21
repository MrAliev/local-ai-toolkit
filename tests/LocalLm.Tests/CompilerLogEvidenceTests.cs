using System.Security.Cryptography;
using System.Text;
using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class CompilerLogEvidenceTests
{
    [Fact]
    public void Raw_occurrences_include_repeated_diagnostics_but_exclude_warnings_and_build_totals()
    {
        const string error = "C:/repo/A.cs(4,9): error CS7036: Missing argument. [App.csproj]";
        var source = string.Join('\n',
            "C:/repo/A.cs(1,1): warning CS0168: Message quotes error CS9999: unrelated.",
            "\u001b[31m" + error + "\u001b[0m",
            error,
            "    2 Error(s)",
            "Ошибок: 2",
            "README says error CS0000 without a diagnostic colon");

        var report = CompilerLogScanner.Scan(source);

        Assert.Equal("errors_found", report.AnalysisStatus);
        Assert.Equal(2, report.DiagnosticLineCount);
        Assert.Single(report.CodeCounts);
        Assert.Equal(2, report.CodeCounts["CS7036"]);
        Assert.Equal([2L, 3L], report.Diagnostics.Select(item => item.SourceLine));
        Assert.Equal([2L, 2L], report.BuildSummaries.Select(item => item.ErrorCount));
        Assert.Equal([4L, 5L], report.BuildSummaries.Select(item => item.SourceLine));
    }

    [Fact]
    public void Arbitrary_fragment_boundaries_preserve_crlf_unicode_source_lines_and_fingerprint()
    {
        const string source = "before 😀\r\nC:/A.cs(1,1): error CS0117: Missing member.\r\n" +
                              "C:/B.cs(2,2): error CS7036: Missing argument.\rfinal";
        using var scanner = new CompilerLogScanner();
        foreach (var character in source)
            scanner.Append(character.ToString());

        var report = scanner.Complete();

        Assert.Equal(2, report.DiagnosticLineCount);
        Assert.Equal([2L, 3L], report.Diagnostics.Select(item => item.SourceLine));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            report.SourceTextSha256);
    }

    [Fact]
    public void Bounded_samples_do_not_cap_the_total_occurrence_count()
    {
        var source = string.Join('\n', Enumerable.Repeat("A.cs(1,1): error CS0117: Missing member.", 45));

        var report = CompilerLogScanner.Scan(source);

        Assert.Equal(45, report.DiagnosticLineCount);
        Assert.Equal(45, report.CodeCounts["CS0117"]);
        Assert.Equal(40, report.Diagnostics.Count);
        Assert.Equal(5, report.OmittedDiagnostics);
        Assert.Equal("errors_found", report.AnalysisStatus);
    }

    [Fact]
    public void An_unparsed_long_line_is_incomplete_evidence_and_cannot_validate_a_clean_model_claim()
    {
        var report = CompilerLogScanner.Scan(new string('x', 70_000) +
                                             " error CS0117: Hidden at the end.\n");

        Assert.Equal("incomplete", report.AnalysisStatus);
        Assert.Equal(1, report.UnparsedLongLines);
        Assert.True(CompilerLogVerification.HasEvidence(report));
        var verification = CompilerLogVerification.Verify(
            "{\"diagnosticLineCount\":0,\"codeCounts\":{},\"buildSummaryErrors\":[]}", report);
        Assert.Equal("failed", verification.Status);
        Assert.Contains("incomplete_source_evidence", verification.Reasons);
    }

    [Theory]
    [InlineData("{\"diagnosticLineCount\":2,\"codeCounts\":{\"CS7036\":2},\"buildSummaryErrors\":[2]}", "passed")]
    [InlineData("{\"diagnosticLineCount\":0,\"codeCounts\":{},\"buildSummaryErrors\":[]}", "failed")]
    [InlineData("{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS7036\":1},\"buildSummaryErrors\":[2]}", "failed")]
    [InlineData("{\"diagnosticLineCount\":2,\"codeCounts\":{\"CS9999\":2},\"buildSummaryErrors\":[2]}", "failed")]
    [InlineData("{\"diagnosticLineCount\":2,\"codeCounts\":{\"CS7036\":2},\"buildSummaryErrors\":[4]}", "failed")]
    [InlineData("No compiler errors; only warnings.", "failed")]
    public void Only_an_exact_inventory_is_verified(string answer, string expectedStatus)
    {
        var report = CompilerLogScanner.Scan(
            "A.cs(1,1): error CS7036: Missing argument.\n" +
            "A.cs(1,1): error CS7036: Missing argument.\n2 Error(s)");

        Assert.Equal(expectedStatus, CompilerLogVerification.Verify(answer, report).Status);
    }
}
