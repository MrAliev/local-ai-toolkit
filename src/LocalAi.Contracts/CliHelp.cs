using LocalAi.Contracts.Localization;

namespace LocalAi.Contracts;

public sealed record CliHelpResult(bool Success, string Text);

/// <summary>
/// A help flag anywhere after the command words asks for that command's help, unless it is the
/// value of an option the command's syntax gives a value.
/// </summary>
public static class CliHelp
{
    public static CliHelpResult? Resolve(string binary, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return null;
        if (IsSelector(arguments[0]))
        {
            var target = string.Join(' ', arguments.Skip(1));
            if (arguments.Skip(1).Any(word => word.StartsWith('-')) || (target.Length > 0 && !KnownPrefix(binary, target)))
                return new(false, OutputCulture.Pick(
                    $"Unknown help arguments. Use {binary} --help or {binary} help <command>.",
                    $"Неизвестные аргументы справки. Используйте {binary} --help или {binary} help <команда>."));
            return new(true, Render(binary, target));
        }

        var words = 0;
        while (words < arguments.Count && KnownPrefix(binary, string.Join(' ', arguments.Take(words + 1)))) words++;
        if (words == 0) return null;
        var command = string.Join(' ', arguments.Take(words));
        var valued = ValuedOptions(binary, command);
        for (var index = words; index < arguments.Count; index++)
        {
            if (IsFlag(arguments[index])) return new(true, Render(binary, command));
            if (valued.Contains(arguments[index]) && index + 1 < arguments.Count && CanBeValue(binary, arguments[index + 1])) index++;
        }
        return null;
    }

    public static string Render(string binary, string command = "") =>
        OutputCulture.Pick("Usage:", "Использование:") + Environment.NewLine
            + string.Join(Environment.NewLine, Selected(binary, command).Select(entry => "  " + entry.Syntax))
            + Environment.NewLine + Environment.NewLine
            + OutputCulture.Pick(
                $"Help: {binary} --help | {binary} <command> --help | {binary} help <command>\nOptional arguments are in brackets. Commands run only when help is not requested.",
                $"Справка: {binary} --help | {binary} <команда> --help | {binary} help <команда>\nНеобязательные аргументы указаны в скобках. Запрос справки не запускает команду.");

    public static bool IsRequest(string binary, IReadOnlyList<string> arguments) => Resolve(binary, arguments) is not null;

    private static UsageOperation[] Selected(string binary, string command)
    {
        var descriptions = Descriptions(binary).ToArray();
        var selected = descriptions.Where(entry => command.Length == 0 || Matches(entry.Operation, command)).ToArray();
        // Detailed operations may share their group's syntax (native, semantic, hook).
        return selected.Length > 0 ? selected : descriptions
            .Where(entry => command.StartsWith(entry.Operation + " ", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Operation.Length).Take(1).ToArray();
    }

    // An option followed by a word in the syntax takes a value: `--root dir` does, `--follow [--interval s]` does not.
    private static HashSet<string> ValuedOptions(string binary, string command)
    {
        var valued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Selected(binary, command))
        {
            var tokens = entry.Syntax!.Split([' ', '[', ']', '(', ')', '|'], StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index + 1 < tokens.Length; index++)
                if (tokens[index].StartsWith("--", StringComparison.Ordinal) && !tokens[index + 1].StartsWith('-'))
                    valued.Add(tokens[index]);
        }
        return valued;
    }

    // codesearch's option parser never takes a token starting with "--" as a value.
    private static bool CanBeValue(string binary, string token) =>
        binary != "codesearch" || !token.StartsWith("--", StringComparison.Ordinal);

    private static bool KnownPrefix(string binary, string prefix) => Descriptions(binary).Any(entry => Matches(entry.Operation, prefix))
        || UsageReportingRegistry.All.Any(entry => entry.Surface == UsageSurface.Cli && entry.Component == binary
            && entry.Operation == prefix && entry.Operation != "unknown_command" && !IsSelector(prefix));

    private static bool IsFlag(string value) => value is "--help" or "-h";
    private static bool IsSelector(string value) => value == "help" || IsFlag(value);
    private static bool Matches(string command, string prefix) => command == prefix || command.StartsWith(prefix + " ", StringComparison.Ordinal);
    private static IEnumerable<UsageOperation> Descriptions(string binary) => UsageReportingRegistry.All
        .Where(entry => entry.Surface == UsageSurface.Cli && entry.Component == binary && entry.Syntax is not null);
}
