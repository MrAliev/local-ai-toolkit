using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Cli.Resources;

namespace LocalAi.Cli;

internal static class DocumentationNotice
{
    internal static string Format(string model, TimeSpan queue, TimeSpan execution, ResidencyShortfall shortfall,
        int? vramPercent, CultureInfo? language = null)
    {
        string Text(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, CliText.Catalogue.Get(key, language ?? CultureInfo.CurrentUICulture), args);
        var mark = shortfall switch
        {
            ResidencyShortfall.Cpu => Text("DocsResidencyCpu"),
            ResidencyShortfall.PartialOffload => Text("DocsResidencyPartial", vramPercent?.ToString(CultureInfo.InvariantCulture) ?? "?"),
            _ => "",
        };
        return Text("DocsModelReceipt", model, mark, queue.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture), execution.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
    }
}
