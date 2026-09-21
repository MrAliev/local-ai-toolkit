using LocalAi.Contracts.Localization;

namespace LocalAi.Contracts;

public sealed record CliHelpResult(bool Success, string Text);

/// <summary>Help accepts only command words and a help selector, never command execution options.</summary>
public static class CliHelp
{
    public static CliHelpResult? Resolve(string binary, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return null;
        var first = IsSelector(arguments[0]);
        // A selector immediately after command words belongs to help. Once options or
        // operands start, their values (including a literal "--help") belong to the command.
        var descriptions = Descriptions(binary).ToArray();
        var selector = first ? 0 : Enumerable.Range(1, arguments.Count - 1)
            .FirstOrDefault(index => IsFlag(arguments[index]) && KnownPrefix(string.Join(' ', arguments.Take(index))), -1);
        if (selector < 0) return null;
        var words = first ? arguments.Skip(1).ToArray() : arguments.Take(selector).ToArray();
        var target = string.Join(' ', words);
        if ((!first && selector != arguments.Count - 1)
            || words.Any(word => word.StartsWith('-'))
            || (target.Length > 0 && !KnownPrefix(target)))
            return new(false, OutputCulture.Pick(
                $"Unknown help arguments. Use {binary} --help or {binary} help <command>.",
                $"Неизвестные аргументы справки. Используйте {binary} --help или {binary} help <команда>."));
        return new(true, Render(binary, target));

        bool KnownPrefix(string prefix) => descriptions.Any(entry => Matches(entry.Operation, prefix))
            || UsageReportingRegistry.All.Any(entry => entry.Surface == UsageSurface.Cli && entry.Component == binary
                && entry.Operation == prefix && entry.Operation != "unknown_command" && !IsSelector(prefix));
    }

    public static string Render(string binary, string command = "")
    {
        var descriptions = Descriptions(binary).ToArray();
        var selected = descriptions.Where(entry => command.Length == 0 || Matches(entry.Operation, command)).ToArray();
        if (selected.Length == 0)
        {
            // Detailed operations may share their group's syntax (native, semantic, hook).
            selected = descriptions.Where(entry => command.StartsWith(entry.Operation + " ", StringComparison.Ordinal))
                .OrderByDescending(entry => entry.Operation.Length).Take(1).ToArray();
        }
        return OutputCulture.Pick("Usage:", "Использование:") + Environment.NewLine
            + string.Join(Environment.NewLine, selected.Select(entry => "  " + entry.Syntax))
            + Environment.NewLine + Environment.NewLine
            + OutputCulture.Pick(
                $"Help: {binary} --help | {binary} <command> --help | {binary} help <command>\nOptional arguments are in brackets. Commands run only when help is not requested.",
                $"Справка: {binary} --help | {binary} <команда> --help | {binary} help <команда>\nНеобязательные аргументы указаны в скобках. Запрос справки не запускает команду.");
    }

    public static bool IsRequest(string binary, IReadOnlyList<string> arguments) => Resolve(binary, arguments) is not null;
    private static bool IsFlag(string value) => value is "--help" or "-h";
    private static bool IsSelector(string value) => value == "help" || IsFlag(value);
    private static bool Matches(string command, string prefix) => command == prefix || command.StartsWith(prefix + " ", StringComparison.Ordinal);
    private static IEnumerable<UsageOperation> Descriptions(string binary) => UsageReportingRegistry.All
        .Where(entry => entry.Surface == UsageSurface.Cli && entry.Component == binary && entry.Syntax is not null);
}
