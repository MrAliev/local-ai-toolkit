using LocalAi.Documentation;
using CodeSearch.Core.Indexing;
using LocalAi.Broker.Client;
using LocalAi.Cli.Resources;
using LocalLm.Core;
using System.Globalization;

namespace LocalAi.Cli;

internal static class DocumentationCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable, CancellationToken cancellationToken,
        string? runtimeRoot = null, Func<DocumentationInput, CancellationToken, Task<DocumentationDraft>>? generate = null)
    {
        var operation = args.FirstOrDefault() ?? "";
        var command = "docs " + operation;
        var root = ""; var limit = 5; var all = false; var namedLimit = false; var standaloneHtml = false;
        string T(string key, params object?[] values) => CliText.Catalogue.Format(key, values);
        int Refuse(string code, string message, int exit)
        {
            if (machineReadable) streams.Out.WriteLine(MachineOutput.Refusal(command, code, message));
            else streams.Error.WriteLine(message);
            return exit;
        }
        if (operation is not ("build" or "apply")) return Refuse("invalid_arguments", T("DocsUsage"), 2);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--root" && root.Length == 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) root = args[++i];
            else if (operation == "build" && args[i] == "--limit" && !namedLimit)
            {
                namedLimit = true;
                if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)
                    return Refuse("invalid_arguments", T("DocsLimitInvalid"), 2);
            }
            else if (operation == "build" && args[i] == "--all" && !all) all = true;
            else if (operation == "build" && args[i] == "--standalone-html" && !standaloneHtml) standaloneHtml = true;
            else return Refuse("invalid_arguments", CliText.CommandUnknownArgument(command, args[i], T("DocsUsage")), 2);
        }
        if (root.Length == 0) return Refuse("invalid_arguments", T("DocsRootRequired"), 2);
        if (all && namedLimit) return Refuse("invalid_arguments", T("DocsSelectionConflict"), 2);
        runtimeRoot ??= RuntimeIndexLayout.DefaultRuntimeRoot;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = RuntimeIndexLayout.Inspect(root, runtimeRoot);
            var store = new DocumentationPackStore(runtimeRoot, identity.RepositoryId,
                htmlMode: standaloneHtml ? DocumentationHtmlMode.Standalone : DocumentationHtmlMode.Shared);
            if (operation == "apply")
            {
                var applied = store.Apply(identity.WorkingRoot.Value, DocumentationWorkspace.Snapshot(identity));
                foreach (var skipped in applied.Skipped) streams.Error.WriteLine(T("DocsApplySkipped", skipped));
                if (machineReadable) streams.Out.WriteLine(MachineOutput.Answer(command, applied));
                else
                {
                    streams.Out.WriteLine(T("DocsApplySummary", applied.Copied, applied.Unchanged, applied.Skipped.Count));
                    streams.Out.WriteLine(T("DocsApplyCopied", applied.Copied, applied.Destination));
                }
                return 0;
            }
            streams.Error.WriteLine(all ? T("DocsAll") : T("DocsSubset", limit));
            var input = DocumentationWorkspace.Capture(root, runtimeRoot, all ? int.MaxValue : limit, cancellationToken);
            streams.Error.WriteLine(T("DocsSelectedCount", input.Units.Count));
            DocumentationGenerator? generator = null;
            generate ??= (content, token) =>
            {
                generator ??= new DocumentationGenerator(new BrokerDocumentationModel(
                    new BrokerLocalModelClient(BrokerClientFactory.CreateDefault(runtimeRoot, new LocalRunProgress(streams.Error, () => DateTimeOffset.UtcNow))),
                    receipt => streams.Error.WriteLine(DocumentationNotice.Format(receipt.Model, receipt.QueueDuration, receipt.ExecutionDuration,
                        receipt.Routing?.ResidencyShortfall ?? LocalAi.Contracts.ResidencyShortfall.None, receipt.Routing?.VramResidentPercent))));
                return generator.GenerateAsync(content, token);
            };
            var report = await store.BuildAsync(input, generate, progress => streams.Error.WriteLine(progress.Phase switch
            {
                "generate" => T("DocsBuildingType", progress.Name, progress.Index, progress.Total),
                "render" => T("DocsRenderingType", progress.Name),
                _ => T("DocsValidatingType", progress.Name)
            }), cancellationToken).ConfigureAwait(false);
            foreach (var unit in report.Units.Where(unit => !unit.Reused))
            {
                streams.Error.WriteLine(T("DocsChangedType", unit.Name, string.Join(" ", unit.Reasons.Select(reason => T("DocsReason_" + reason)))));
                if (unit.Error is null) continue;
                streams.Error.WriteLine(T("DocsValidationFailed", unit.Name, unit.Error));
                streams.Error.WriteLine(T(unit.PreviousKept ? "DocsPreviousKept" : "DocsNoPrevious", unit.Name));
            }
            streams.Error.WriteLine(T("DocsKeptCount", report.Reused));
            streams.Error.WriteLine(T("DocsBuildFinished", report.ElapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)));
            streams.Error.WriteLine(T("DocsPackageReady", report.PackagePath));
            if (DocumentationWorkspace.Snapshot(RuntimeIndexLayout.Inspect(root, runtimeRoot)) != input.Snapshot) throw new DocumentationValidationException(["sources_changed"]);
            if (machineReadable) streams.Out.WriteLine(MachineOutput.Answer(command, report));
            else streams.Out.WriteLine(report.PackagePath);
            // The command produced a reviewable report, including any failed units. The report's
            // Failed count is the gate an unattended caller must check before applying it.
            return 0;
        }
        catch (OperationCanceledException)
        {
            streams.Error.WriteLine(T("DocsCheckpointsKept"));
            throw;
        }
        catch (DocumentationValidationException error)
        {
            var code = error.Errors.FirstOrDefault() ?? "documentation_failed";
            var key = code switch { "index_outdated" => "DocsIndexOutdated", "sources_changed" => "DocsSourcesChanged", "package_missing" => "DocsPackageMissing",
                "build_already_running" => "DocsBuildAlreadyRunning", "runtime_inside_repository" => "DocsRuntimeInsideRepository", "destination_link" => "DocsDestinationLink", _ => "DocsOperationFailed" };
            return Refuse(code, T(key, key == "DocsOperationFailed" ? error.Message : '"' + (error.Data["path"] as string ?? root) + '"'), 65);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Refuse("documentation_failed", T("DocsOperationFailed", error.Message), 65);
        }
    }
}
