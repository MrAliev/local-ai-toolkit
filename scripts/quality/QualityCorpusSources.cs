using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LocalAi.TestSupport;

public static class QualityCorpusSources
{
    public static IEnumerable<(string Path, string Text)> Files(string corpus)
    {
        if (corpus is not ("csharp" or "typescript" or "python" or "mixed"))
            throw new ArgumentException("quality_corpus_unknown", nameof(corpus));
        yield return (".gitignore", "bin/\nobj/\nindex.scip\n.scip/\n");
        if (corpus is "csharp" or "mixed")
            foreach (var file in CSharpFiles()) yield return file;
        if (corpus is "typescript" or "mixed")
            foreach (var file in SyntheticFrontendRepository.Files(10).Where(file => !file.Path.EndsWith(".py", StringComparison.Ordinal)))
                yield return file;
        if (corpus is "python" or "mixed")
        {
            foreach (var file in SyntheticFrontendRepository.Files(0).Where(file => file.Path.EndsWith(".py", StringComparison.Ordinal)))
                yield return file;
            foreach (var file in PythonFiles()) yield return file;
        }
    }

    public static void Write(string root, string corpus)
    {
        Directory.CreateDirectory(root);
        foreach (var (path, text) in Files(corpus))
        {
            var destination = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, text.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        }
    }

    private static readonly string[] Neighbours =
        ["invoice", "shipment", "customer", "warehouse", "payment", "refund", "catalog", "pricing", "discount", "inventory"];

    private static IEnumerable<(string Path, string Text)> CSharpFiles()
    {
        yield return ("QualityCorpus.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        yield return ("src/pricing/WeightPricing.cs", """
            namespace Corpus.Pricing;

            public static class WeightPricing
            {
                // A parcel is charged by weight; oversized handling is a fixed supplement.
                public static decimal Quote(decimal kilograms, bool oversized)
                {
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(kilograms);
                    var charge = 2.5m + decimal.Ceiling(kilograms) * 0.75m;
                    return charge + (oversized ? 3m : 0m);
                }
            }
            """);
        yield return ("src/pricing/VolumePricing.cs", """
            namespace Corpus.Pricing;

            public static class VolumePricing
            {
                // Storage space is billed by cubic metres, independently of parcel weight.
                public static decimal Quote(decimal length, decimal width, decimal height)
                {
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
                    return decimal.Ceiling(length * width * height) * 0.4m;
                }
            }
            """);
        yield return ("src/retries/RetrySchedule.cs", """
            namespace Corpus.Retries;

            public static class RetrySchedule
            {
                // Permanent failures are never retried. Transient failures back off to at most a minute.
                public static int? GetDelaySeconds(int attempt, bool permanentFailure)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(attempt);
                    if (permanentFailure) return null;
                    return Math.Min(60, 1 << Math.Min(attempt, 6));
                }
            }
            """);
        var manifest = new StringBuilder("""
            namespace Corpus.Manifests;

            public static class ManifestReader
            {
                // Validate the complete 32-column manifest and collect every empty field.
                // The caller needs all corrections at once instead of one failure per retry.
                public static IReadOnlyList<string> ValidateAllFields(string[] fields)
                {
                    ArgumentNullException.ThrowIfNull(fields);
                    var errors = new List<string>();
                    if (fields.Length != 32) errors.Add("Expected 32 columns.");
            """ + "\n");
        for (var index = 0; index < 32; index++)
        {
            manifest.Append("        if (fields.Length <= ").Append(index)
                .Append(" || string.IsNullOrWhiteSpace(fields[").Append(index).Append("]))\n")
                .Append("        {\n            errors.Add(\"Column ").Append(index + 1)
                .Append(" is empty.\");\n        }\n");
        }
        manifest.Append("        return errors;\n    }\n}\n");
        yield return ("src/manifests/ManifestReader.cs", manifest.ToString());
        foreach (var name in Neighbours)
        {
            var type = char.ToUpperInvariant(name[0]) + name[1..];
            yield return ($"src/features/{type}Lookup.cs", $$"""
                namespace Corpus.Features;

                public static class {{type}}Lookup
                {
                    // Find one {{name}} label by its stable key; missing records return no label.
                    public static string? FindByKey(IReadOnlyDictionary<string, string> records, string key)
                    {
                        return records.TryGetValue(key, out var label) ? label : null;
                    }

                    public static string NormalizeLabel(string label)
                    {
                        return label.Trim();
                    }
                }
                """);
        }
    }

    private static IEnumerable<(string Path, string Text)> PythonFiles()
    {
        yield return ("src/shared/lib/currency_policy.py", """
            from decimal import Decimal, ROUND_HALF_EVEN

            def round_for_statistics(value: str) -> Decimal:
                # Statistical reports use ties-to-even, unlike invoice minor units.
                return Decimal(value).quantize(Decimal("0.01"), rounding=ROUND_HALF_EVEN)

            def sum_report_values(values: list[str]) -> Decimal:
                # Keep exact decimal values until the final reporting boundary.
                return sum((Decimal(value) for value in values), Decimal("0"))
            """);
        yield return ("src/shared/lib/manifest.py", """
            def missing_fields(row: dict[str, str], required: list[str]) -> list[str]:
                # Collect absent and blank fields while retaining the declared column order.
                return [name for name in required if not row.get(name, "").strip()]

            def accepted_rows(rows: list[dict[str, str]], required: list[str]) -> list[dict[str, str]]:
                # A row is accepted only when every required field is present and non-blank.
                return [row for row in rows if not missing_fields(row, required)]
            """);
        foreach (var name in Neighbours)
        {
            yield return ($"src/features/{name}_ledger.py", $$"""
                from decimal import Decimal

                def total_{{name}}(records: list[dict[str, str]]) -> Decimal:
                    # Sum exact {{name}} amounts; cancelled records are left out.
                    return sum((Decimal(row["amount"]) for row in records
                                if row.get("status") != "cancelled"), Decimal("0"))

                def find_{{name}}(records: list[dict[str, str]], key: str) -> dict[str, str] | None:
                    # Return the first {{name}} record with this stable identifier.
                    return next((row for row in records if row.get("id") == key), None)
                """);
        }
    }
}
