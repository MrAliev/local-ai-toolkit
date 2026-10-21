using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

public sealed record CompilerModelVerification(string Status, IReadOnlyList<string> Reasons);

/// <summary>
/// The model is never the authority for diagnostic counts. Only a complete, exact
/// reconciliation is accepted; prose and partial or invented inventories are rejected.
/// Rejected text remains in the broker's job archive, not in the delivered diagnosis.
/// </summary>
internal static partial class CompilerLogVerification
{
    internal const string Instruction = """
        If the material contains compiler/build error diagnostics or an error-count summary,
        return ONLY a JSON object with exactly these three fields:
        diagnosticLineCount (a nonnegative integer), codeCounts (an object mapping each error code
        to its positive integer occurrence count), buildSummaryErrors (an array of declared totals).
        Count error diagnostic line occurrences,
        not warnings or unique messages. Use "uncoded" for an error diagnostic without a code.
        Keep declared build error totals separate in buildSummaryErrors, in source order;
        use an empty array if none are present. Do not count summary lines as diagnostics.
        No prose, additional fields or code fences. When merging, reconcile overlapping evidence.
        """;

    internal static bool HasEvidence(CompilerLogReport report) =>
        report.DiagnosticLineCount > 0 || report.BuildSummaries.Any(summary => summary.ErrorCount > 0) ||
        report.OmittedBuildSummaries > 0 ||
        report.AnalysisStatus == "incomplete";

    internal static bool ContainsDiagnosticClaim(string answer) => DiagnosticCode().IsMatch(answer) ||
        answer.Contains("\"diagnosticLineCount\"", StringComparison.Ordinal) ||
        answer.Contains("\"codeCounts\"", StringComparison.Ordinal);

    internal static CompilerModelVerification Verify(string answer, CompilerLogReport source)
    {
        var reasons = new List<string>();
        if (source.AnalysisStatus == "incomplete" || source.OmittedBuildSummaries > 0)
            reasons.Add("incomplete_source_evidence");
        try
        {
            using var document = JsonDocument.Parse(answer, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failed("response_not_inventory", reasons);
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3 ||
                properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                !root.TryGetProperty("diagnosticLineCount", out var total) ||
                !root.TryGetProperty("codeCounts", out var codes) ||
                !root.TryGetProperty("buildSummaryErrors", out var summaries))
                return Failed("response_schema_mismatch", reasons);

            if (!total.TryGetInt64(out var claimedTotal) || claimedTotal != source.DiagnosticLineCount)
                reasons.Add("diagnostic_count_mismatch");

            if (codes.ValueKind != JsonValueKind.Object)
                reasons.Add("code_counts_not_object");
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var code in codes.EnumerateObject())
                {
                    if (!seen.Add(code.Name))
                        reasons.Add("duplicate_code");
                    if (!source.CodeCounts.TryGetValue(code.Name, out var expected))
                        reasons.Add("unverified_code");
                    else if (!code.Value.TryGetInt64(out var count) || count != expected)
                        reasons.Add("code_count_mismatch:" + code.Name);
                }
                foreach (var code in source.CodeCounts.Keys)
                    if (!seen.Contains(code)) reasons.Add("omitted_code:" + code);
            }

            if (summaries.ValueKind != JsonValueKind.Array)
                reasons.Add("build_summary_not_array");
            else
            {
                var claimed = summaries.EnumerateArray().ToArray();
                if (claimed.Length != source.BuildSummaries.Count ||
                    claimed.Where((item, index) => index < source.BuildSummaries.Count &&
                        (!item.TryGetInt64(out var count) || count != source.BuildSummaries[index].ErrorCount)).Any())
                    reasons.Add("build_summary_mismatch");
            }
        }
        catch (JsonException)
        {
            reasons.Add("response_not_json");
        }
        catch (InvalidOperationException)
        {
            reasons.Add("response_schema_mismatch");
        }
        return new CompilerModelVerification(reasons.Count == 0 ? "passed" : "failed", reasons);
    }

    internal static string Render(CompilerLogReport report, CompilerModelVerification verification)
    {
        var text = new StringBuilder();
        text.AppendLine(LocalLmText.LogErrorLineCount(report.DiagnosticLineCount));
        if (report.CodeCounts.Count > 0)
            text.AppendLine(LocalLmText.LogErrorCodeCounts(string.Join("; ", report.CodeCounts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + " × " + pair.Value.ToString(CultureInfo.InvariantCulture)))));
        else
            text.AppendLine(LocalLmText.LogNoRecognizedErrors);
        foreach (var summary in report.BuildSummaries)
            text.AppendLine(LocalLmText.LogMsBuildSummary(
                LocalLmText.LogDiagnosticLine(summary.SourceLine, summary.Text + (summary.Truncated ? " …" : string.Empty))));
        if (report.OmittedBuildSummaries > 0)
            text.AppendLine(LocalLmText.LogOmittedBuildSummaries(report.OmittedBuildSummaries));
        text.AppendLine(LocalLmText.LogErrorSamples(report.Diagnostics.Count, report.DiagnosticLineCount));
        foreach (var diagnostic in report.Diagnostics)
            text.AppendLine(LocalLmText.LogDiagnosticLine(diagnostic.SourceLine,
                diagnostic.Text + (diagnostic.Truncated ? " …" : string.Empty)));
        if (report.UnparsedLongLines > 0)
            text.AppendLine(LocalLmText.LogUnparsedLongLines(report.UnparsedLongLines));
        text.AppendLine("analysis_status=" + report.AnalysisStatus);
        text.AppendLine("model_validation=" + verification.Status);
        text.AppendLine(verification.Status == "passed"
            ? LocalLmText.LogModelVerificationPassed : LocalLmText.LogModelVerificationFailed);
        if (verification.Reasons.Count > 0)
            text.AppendLine("model_validation_reason=" + string.Join(";", verification.Reasons));
        text.Append("source_text_sha256=" + report.SourceTextSha256);
        return text.ToString();
    }

    private static CompilerModelVerification Failed(string reason, List<string> reasons)
    {
        reasons.Add(reason);
        return new CompilerModelVerification("failed", reasons);
    }

    [GeneratedRegex(@"\b(?:fatal\s+error|error|ошибка|ошибки)\s+\*{0,2}(?:CS|FS|BC|MSB|NETSDK|NU|TS|LNK|CA|IDE|RZ|SYSLIB|C)\d{3,8}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticCode();
}
