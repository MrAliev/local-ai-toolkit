using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Wherever a person reads the product's name it is two words, with AI in capitals: "Local AI".
/// The spelling LocalAi stays only where it names something a machine matches - a directory, a
/// marker a later run looks for, a certificate's name - until those are renamed with the
/// compatibility they need.
/// </summary>
public sealed class TheProductIsCalledLocalAITests
{
    /// <summary>The name as a word: not inside an identifier, a path, a namespace or a file name.</summary>
    private static readonly Regex Word = new(
        @"(?<![\w\\/%=.-])LocalAi(?![\w\\/-]|\.\w)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// String literals that are names a machine matches rather than prose: the runtime directory,
    /// the markers the installer and the hooks recognise their own work by, a thread's name, and
    /// the placeholder publisher of the signing policy.
    /// </summary>
    private static readonly string[] MachineNames =
    [
        "# LocalAi managed dispatcher",
        "# LocalAi managed Git hooks",
        "LocalAi installer run directory",
        "LocalAi installer transaction lease",
    ];

    /// <summary>A literal that is the name alone is a directory's name; "Local AI" is never one.</summary>
    private static bool IsMachineName(string literal) =>
        literal == "LocalAi" || MachineNames.Any(name => literal.StartsWith(name, StringComparison.Ordinal));

    [Fact]
    public void No_string_in_the_product_spells_the_name_as_one_word()
    {
        var offenders = SourceFiles("src", "*.cs")
            .SelectMany(file => StringLiterals(File.ReadAllText(file))
                .Where(literal => !IsMachineName(literal.Value))
                .Where(literal => Word.IsMatch(literal.Value))
                .Select(literal => $"{Relative(file)}:{literal.Line}  {Shorten(literal.Value)}"))
            .ToArray();

        Assert.True(offenders.Length == 0, Message("string literals", offenders));
    }

    [Fact]
    public void No_resource_string_spells_the_name_as_one_word()
    {
        var offenders = SourceFiles("src", "*.resx")
            .SelectMany(file => XDocument.Load(file).Descendants("data")
                .Select(data => (Name: data.Attribute("name")?.Value, Value: data.Element("value")?.Value ?? string.Empty))
                .Where(entry => Word.IsMatch(entry.Value))
                .Select(entry => $"{Relative(file)}  {entry.Name}: {Shorten(entry.Value)}"))
            .ToArray();

        Assert.True(offenders.Length == 0, Message("resource strings", offenders));
    }

    [Fact]
    public void No_window_title_or_label_spells_the_name_as_one_word()
    {
        var offenders = SourceFiles(Path.Combine("src", "LocalAi.Installer"), "*.xaml")
            .SelectMany(file => XDocument.Load(file).Descendants()
                .SelectMany(element => element.Attributes())
                .Where(attribute => attribute.Name.LocalName is "Title" or "Text" or "Content" or "Header" or "ToolTip")
                .Where(attribute => Word.IsMatch(attribute.Value))
                .Select(attribute => $"{Relative(file)}  {attribute.Name.LocalName}=\"{attribute.Value}\""))
            .ToArray();

        Assert.True(offenders.Length == 0, Message("markup", offenders));
    }

    /// <summary>Prose only: code spans, code blocks and link targets carry commands and paths.</summary>
    [Fact]
    public void No_document_spells_the_name_as_one_word_in_its_prose()
    {
        var root = RepositoryRoot();
        var documents = new[] { "README.md", "README.ru.md", "SECURITY.md", "SECURITY.ru.md", "CONTRIBUTING.md", "CONTRIBUTING.ru.md" }
            .Select(name => Path.Combine(root, name))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories));
        var offenders = documents
            .SelectMany(file => Prose(File.ReadAllText(file))
                .Where(line => Word.IsMatch(line.Text))
                .Select(line => $"{Relative(file)}:{line.Number}  {Shorten(line.Text)}"))
            .ToArray();

        Assert.True(offenders.Length == 0, Message("documents", offenders));
    }

    /// <summary>
    /// The lines of a markdown document with fenced blocks dropped and inline code, link targets
    /// and HTML comments blanked, so what is left is what a reader sees as text.
    /// </summary>
    private static IEnumerable<(int Number, string Text)> Prose(string document)
    {
        var fence = false;
        var number = 0;
        foreach (var raw in document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            number++;
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }

            if (fence)
            {
                continue;
            }

            var text = Regex.Replace(raw, "`[^`]*`", " ");
            text = Regex.Replace(text, @"\]\([^)]*\)", "]");
            text = Regex.Replace(text, "<!--.*?-->", " ");
            yield return (number, text);
        }
    }

    /// <summary>
    /// The contents of every string literal in a C# file, comments skipped: regular, verbatim,
    /// interpolated and raw literals, each with the line it starts on.
    /// </summary>
    internal static IEnumerable<(int Line, string Value)> StringLiterals(string code)
    {
        var line = 1;
        var index = 0;
        while (index < code.Length)
        {
            var c = code[index];
            if (c == '\n')
            {
                line++;
                index++;
            }
            else if (c == '/' && Next(code, index) == '/')
            {
                while (index < code.Length && code[index] != '\n') index++;
            }
            else if (c == '/' && Next(code, index) == '*')
            {
                var end = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
                line += Count(code, index, end);
                index = end;
            }
            else if (c == '\'')
            {
                index++;
                while (index < code.Length && code[index] != '\'')
                {
                    index += code[index] == '\\' ? 2 : 1;
                }

                index++;
            }
            else if (c == '"' || ((c is '@' or '$') && Opens(code, index)))
            {
                var start = index;
                var verbatim = false;
                while (code[index] is '@' or '$')
                {
                    verbatim |= code[index] == '@';
                    index++;
                }

                var quotes = 0;
                while (index + quotes < code.Length && code[index + quotes] == '"') quotes++;
                var startLine = line;
                var value = new StringBuilder();
                if (quotes >= 3)
                {
                    var closing = new string('"', quotes);
                    var end = code.IndexOf(closing, index + quotes, StringComparison.Ordinal);
                    end = end < 0 ? code.Length : end;
                    value.Append(code, index + quotes, end - index - quotes);
                    index = Math.Min(code.Length, end + quotes);
                }
                else
                {
                    index++;
                    while (index < code.Length)
                    {
                        var d = code[index];
                        if (verbatim && d == '"' && Next(code, index) == '"')
                        {
                            value.Append('"');
                            index += 2;
                        }
                        else if (d == '"' || (!verbatim && d == '\n'))
                        {
                            index++;
                            break;
                        }
                        else if (!verbatim && d == '\\')
                        {
                            value.Append(Next(code, index) == 'n' ? '\n' : Next(code, index));
                            index += 2;
                        }
                        else
                        {
                            value.Append(d);
                            index++;
                        }
                    }
                }

                line += Count(code, start, index);
                yield return (startLine, value.ToString());
            }
            else
            {
                index++;
            }
        }
    }

    private static bool Opens(string code, int index)
    {
        while (index < code.Length && code[index] is '@' or '$') index++;
        return index < code.Length && code[index] == '"';
    }

    private static char Next(string code, int index) => index + 1 < code.Length ? code[index + 1] : '\0';

    private static int Count(string code, int from, int to) =>
        code.AsSpan(from, Math.Max(0, Math.Min(to, code.Length) - from)).Count('\n');

    private static IEnumerable<string> SourceFiles(string folder, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), folder), pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Message(string what, string[] offenders) =>
        $"{offenders.Length} {what} spell the product's name as one word; a person reads it as Local AI:" +
        Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(40));

    private static string Shorten(string text)
    {
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length <= 110 ? flat : flat[..110] + "…";
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
