using System.Diagnostics;
using System.Text;

namespace CodeSearch.Tests;

/// <summary>
/// What this repository stores, checked rather than remembered.
///
/// The rule is not the one CONTRIBUTING states about the working tree. `core.autocrlf` decides
/// what a checkout looks like on each machine, so working-tree endings are that machine's business
/// and a test about them would pass here and fail on a colleague. What git stores is the same for
/// everybody: line feeds, for every file it recognises as text.
///
/// A file it does not recognise as text is the interesting case, and it is how this test was
/// earned. `CodeSearchUsage.cs` held twenty-three lines ending in a doubled carriage return —
/// `\r\r\n`, the shape a mangled backslash leaves behind — and git classified a C# source file as
/// binary because of them. Nothing normalised it after that, so the stray bytes were stored; the
/// usage text it prints carried a byte a terminal hides and a redirected log does not; and git
/// stopped diffing and merging the file as text, which is the part that would have hurt later.
/// </summary>
public sealed class LineEndingsTests
{
    /// <summary>
    /// Every binary this repository tracks: eight images, the installer's icon, and the release
    /// signing key. Named rather than detected, so a source file that becomes unreadable to git is
    /// a failure here rather than a new entry in a heuristic.
    /// </summary>
    private static readonly string[] BinaryExtensions = [".png", ".ico", ".der"];

    [Fact]
    public void Git_stores_every_source_and_document_as_text()
    {
        var opaque = Eol()
            .Where(file => file.Index == "-text")
            .Where(file => !BinaryExtensions.Contains(
                Path.GetExtension(file.Path),
                StringComparer.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToArray();

        Assert.True(
            opaque.Length == 0,
            "git classifies these as binary, so nothing normalises them and it will not diff or " +
            "merge them as text. A carriage return outside a line ending is what usually does " +
            "it:" + Environment.NewLine + string.Join(Environment.NewLine, opaque));
    }

    [Fact]
    public void Nothing_is_stored_with_carriage_returns_in_its_line_endings()
    {
        var stored = Eol()
            .Where(file => file.Index is "crlf" or "mixed")
            .Select(file => $"{file.Path}: i/{file.Index}")
            .ToArray();

        Assert.True(
            stored.Length == 0,
            "Stored with carriage returns, so every checkout of them differs from every other:" +
            Environment.NewLine + string.Join(Environment.NewLine, stored));
    }

    /// <summary>
    /// `git ls-files --eol` prints `i/&lt;eol&gt; w/&lt;eol&gt; attr/&lt;attr&gt;` and the path. The
    /// index column is the one that is the same on every machine.
    /// </summary>
    private static IEnumerable<(string Index, string Path)> Eol()
    {
        foreach (var line in Git("ls-files", "--eol"))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }

            var index = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            yield return (index["i/".Length..], line[(tab + 1)..]);
        }
    }

    private static string[] Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
    }

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
