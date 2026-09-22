using System.Globalization;
using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;
using LocalAi.Contracts.Security;

namespace LocalAi.Cli;

internal static class SemanticImpactCommand
{
    internal static int Execute(string[] args, CommandStreams streams, bool machineReadable,
        CancellationToken cancellationToken, string? runtimeRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int Refuse(string code, string message, int exit = 2)
        {
            UsageCall.Current?.Refused(code);
            if (machineReadable) streams.Out.WriteLine(MachineOutput.Refusal("semantic impact", code, message));
            else streams.Error.WriteLine(message);
            return exit;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("--root" or "--path" or "--line" or "--column" or "--limit") ||
                i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", LocalAiCommandSyntax.SemanticImpact);
            i++;
        }
        var path = options.GetValueOrDefault("--path");
        if (path is null || !int.TryParse(options.GetValueOrDefault("--line"), NumberStyles.None, CultureInfo.InvariantCulture, out var line) ||
            !int.TryParse(options.GetValueOrDefault("--column", "1"), NumberStyles.None, CultureInfo.InvariantCulture, out var column) ||
            !int.TryParse(options.GetValueOrDefault("--limit", "200"), NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 10_000)
            return Refuse("invalid_arguments", LocalAiCommandSyntax.SemanticImpact);
        if (!SemanticDocumentPath.IsRepositoryRelative(path)) return Refuse("invalid_path", CliText.PathNotRelative(path));
        if (!SourcePosition.TryFromOneBased(line, column, out var position))
            return Refuse("invalid_position", CliText.PositionNotFromOne(line, column));
        try
        {
            var report = new SemanticNavigationGateway(runtimeRoot: runtimeRoot).AnalyzeImpact(path, position.Line,
                position.Utf16Column, limit, options.GetValueOrDefault("--root"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            UsageCall.Current?.NoInference();
            var data = SemanticImpactOutput.Create(report);
            if (machineReadable) streams.Out.WriteLine(MachineOutput.Answer("semantic impact", data));
            else
            {
                streams.Out.WriteLine(OutputCulture.Pick(
                    "Direct indexed evidence only; absence is not proof of no impact. Explain cited evidence separately from hypotheses.",
                    "Только прямые связи индекса; отсутствие связи не доказывает отсутствие влияния. Объясняйте подтверждённые связи отдельно от гипотез."));
                streams.Out.WriteLine(UntrustedContent.Wrap(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), "semantic impact"));
            }
            return 0;
        }
        catch (SemanticNavigationNotReadyException error) { return Refuse("semantic_navigation_not_ready", error.Message, 65); }
        catch (SemanticSnapshotMismatchException error) { return Refuse("semantic_snapshot_mismatch", error.Message, 65); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("impact_", StringComparison.Ordinal))
        {
            return Refuse(error.Message, OutputCulture.Pick(
                "Impact analysis requires an indexed, precise C# symbol and source hashes. " + error.Message,
                "Для анализа нужен точный C#-символ в индексе и хеши исходников. " + error.Message), 65);
        }
    }
}
