using System.Globalization;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalLm.Core;
using LocalLm.Core.Resources;

namespace LocalAi.Cli;

internal static class DocumentSearchCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CommandStreams streams, bool machineReadable,
        CancellationToken cancellationToken, LocalTasks? tasks = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var indexing = args.FirstOrDefault() == "index";
        var command = "documents " + (indexing ? "index" : "search");
        var syntax = indexing ? LocalAiCommandSyntax.DocumentsIndex : LocalAiCommandSyntax.DocumentsSearch;
        int Refuse(string code, string message, int exit = 2)
        {
            if (exit is 70 or 73 or 75) UsageCall.Current?.Failed(code);
            else UsageCall.Current?.Refused(code);
            if (machineReadable) streams.Out.WriteLine(MachineOutput.Refusal(command, code, message));
            else streams.Error.WriteLine(code + ": " + message);
            return exit;
        }
        if (args.Length == 0 || args[0] is not ("index" or "search"))
            return Refuse("invalid_arguments", LocalAiCommandSyntax.DocumentsIndex + "\n" + LocalAiCommandSyntax.DocumentsSearch);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            var allowed = args[i] == "--index" || (indexing ? args[i] is "--source" or "--model" or "--pdf-mode" : args[i] is "--query" or "--limit");
            if (!allowed || i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return Refuse("invalid_arguments", syntax);
        }
        if (!options.TryGetValue("--index", out var index) || !options.ContainsKey(indexing ? "--source" : "--query"))
            return Refuse("invalid_arguments", syntax);
        var limit = 10;
        if (!indexing && (!int.TryParse(options.GetValueOrDefault("--limit", "10"), NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit is < 1 or > 100)) return Refuse("invalid_limit", LocalLmText.Get("DocumentsLimitInvalid"));
        try
        {
            object result;
            if (indexing)
            {
                var observer = new LocalRunProgress(streams.Error, static () => DateTimeOffset.UtcNow);
                tasks ??= new LocalTasks(new BrokerLocalModelClient(BrokerClientFactory.CreateDefault(observer: observer)), observer);
                var mode = options.GetValueOrDefault("--pdf-mode", "Ocr");
                var summary = await DocumentSearchIndex.RefreshAsync(tasks, options["--source"], index, options.GetValueOrDefault("--model"), cancellationToken, mode);
                result = summary;
                if (!machineReadable) streams.Out.WriteLine(LocalLmText.Catalogue.Format("DocumentsIndexReady", summary.Documents, summary.Pages));
                if (!machineReadable && mode == "TextLayer") streams.Out.WriteLine(LocalLmText.Get("DocumentsTextLayerNotice"));
                if (!machineReadable && summary.DocxDocuments > 0) streams.Out.WriteLine(LocalLmText.Get("DocumentsDocxLocationNotice"));
                if (!machineReadable && summary.ExtractionNotices.Length > 0) streams.Out.WriteLine(LocalLmText.Get("DocumentsDocxOmissions"));
            }
            else
            {
                var found = await DocumentSearchIndex.SearchAsync(index, options["--query"], limit, cancellationToken);
                result = found;
                if (!machineReadable)
                {
                    streams.Out.WriteLine(LocalLmText.Get("DocumentsSearchMethod"));
                    streams.Out.WriteLine(LocalLmText.Catalogue.Format("DocumentsSearchSummary", found.Hits.Length, found.ExcludedSources.Length));
                    streams.Out.WriteLine(LocalLmText.Get("DocumentsHitExcerpt"));
                    if (found.Hits.Any(hit => hit.TextKind == "pdf-text")) streams.Out.WriteLine(LocalLmText.Get("DocumentsTextLayerNotice"));
                    if (found.Hits.Any(hit => hit.TextKind == "docx-text")) streams.Out.WriteLine(LocalLmText.Get("DocumentsDocxLocationNotice"));
                    if (found.ExtractionNotices.Length > 0) streams.Out.WriteLine(LocalLmText.Get("DocumentsDocxOmissions"));
                    if (found.ExcludedSources.Length > 0) streams.Out.WriteLine(LocalLmText.Get("DocumentsRefreshHint"));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.SerializeToElement(result, DocumentSearchIndex.JsonOptions);
            streams.Out.WriteLine(machineReadable ? MachineOutput.Answer(command, json) : UntrustedContent.Wrap(json.GetRawText(), command));
            return 0;
        }
        catch (DocumentIndexException error) { return Refuse(error.Code, error.Message, error.Code == "index_busy" ? 75 : 65); }
        catch (BrokerJobFailedException error) { return Refuse("local_model_failed", error.Message, 70); }
        catch (BrokerBootstrapException error) { return Refuse(error.Code, error.Message, 75); }
        catch (ArgumentException error) { return Refuse("input_rejected", error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Refuse("document_io_failed", error.Message, 73); }
    }
}
