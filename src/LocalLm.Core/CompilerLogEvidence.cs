using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalLm.Core;

/// <summary>
/// Deterministic evidence from the source log. Diagnostic counts are raw line
/// occurrences, not deduplicated errors or the totals reported by the build tool.
/// </summary>
public sealed record CompilerLogReport(
    string AnalysisStatus,
    long DiagnosticLineCount,
    IReadOnlyDictionary<string, long> CodeCounts,
    IReadOnlyList<CompilerLogDiagnostic> Diagnostics,
    IReadOnlyList<CompilerLogBuildSummary> BuildSummaries,
    long OmittedDiagnostics,
    long UnparsedLongLines,
    string SourceTextSha256)
{
    public long OmittedBuildSummaries { get; init; }
}

public sealed record CompilerLogDiagnostic(
    long SourceLine,
    string Code,
    string Text,
    bool Truncated);

public sealed record CompilerLogBuildSummary(
    long SourceLine,
    long ErrorCount,
    string Text)
{
    public bool Truncated { get; init; }
}

/// <summary>
/// Consumes source text exactly once. Callers must append only the unique part
/// of each fragment, excluding any overlap used for model context.
/// </summary>
internal sealed class CompilerLogScanner : IDisposable
{
    private const int MaximumLineCharacters = 64 * 1024;
    private const int MaximumSamples = 40;
    private const int MaximumSampleCharacters = 2048;

    private static readonly Regex AnsiEscapePattern = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x9B[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[ -/]*[@-~]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Read the first diagnostic severity so that an error quoted inside a
    // warning's message cannot turn that warning into a compiler error.
    private static readonly Regex DiagnosticPattern = new(
        @"\b(?<severity>fatal[ \t]+error|error|warning)(?:[ \t]+(?<code>[A-Z]+[0-9]+))?[ \t]*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex KnownCompilerCodePattern = new(
        @"^(?:CS|FS|BC|MSB|NETSDK|NU|TS|C|LNK|CA|IDE|RZ|SYSLIB|IL|AD)[0-9]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex CompilerOriginPattern = new(
        @"(?:\(\s*[0-9]+(?:\s*,\s*[0-9]+)*\s*\)" +
        @"|:[ \t]*[0-9]+(?:[ \t]*:[ \t]*[0-9]+)?" +
        @"|\b(?:csc|vbc|fsc|cl|link|msbuild)(?:\.exe)?" +
        @"|\.(?:csproj|vbproj|fsproj|vcxproj))[ \t]*:[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex BuildSummaryPattern = new(
        @"^\s*(?:(?<count>[0-9]+)[ \t]+(?:Error(?:\(s\)|s)?|ошиб(?:ка|ки|ок))|(?:Error(?:\(s\)|s)?|ошиб(?:ка|ки|ок))[ \t]*:[ \t]*(?<count>[0-9]+))\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly StringBuilder _line = new();
    private readonly Dictionary<string, long> _codeCounts = new(StringComparer.Ordinal);
    private readonly List<CompilerLogDiagnostic> _diagnostics = new();
    private readonly List<CompilerLogBuildSummary> _buildSummaries = new();
    private readonly IncrementalHash _sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Encoder _utf8Encoder = Encoding.UTF8.GetEncoder();
    private readonly byte[] _encodingBuffer = new byte[8192];

    private long _sourceLine = 1;
    private long _diagnosticLineCount;
    private long _buildSummaryCount;
    private long _unparsedLongLines;
    private bool _isFirstInput = true;
    private bool _skipLineFeed;
    private bool _discardingLongLine;
    private bool _incomplete;
    private bool _disposed;
    private CompilerLogReport? _report;

    public static CompilerLogReport Scan(string content)
    {
        using var scanner = new CompilerLogScanner();
        scanner.Append(content);
        return scanner.Complete();
    }

    public void Append(string uniqueText)
    {
        ArgumentNullException.ThrowIfNull(uniqueText);
        ThrowIfUnavailable();

        var characters = uniqueText.AsSpan();
        if (characters.IsEmpty)
        {
            return;
        }

        if (_isFirstInput)
        {
            _isFirstInput = false;
            // A decoded file BOM is not part of the source text being hashed.
            if (characters[0] == '\uFEFF')
            {
                characters = characters[1..];
            }
        }

        AppendHash(characters);

        foreach (var character in characters)
        {
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (character == '\n')
                {
                    continue;
                }
            }

            if (character == '\r')
            {
                CompleteLine();
                _skipLineFeed = true;
            }
            else if (character == '\n')
            {
                CompleteLine();
            }
            else if (!_discardingLongLine)
            {
                if (_line.Length == MaximumLineCharacters)
                {
                    _line.Clear();
                    _discardingLongLine = true;
                    _incomplete = true;
                    _unparsedLongLines++;
                }
                else
                {
                    _line.Append(character);
                }
            }
        }
    }

    public CompilerLogReport Complete()
    {
        if (_report is not null)
        {
            return _report;
        }

        ThrowIfUnavailable();
        if (_line.Length > 0 || _discardingLongLine)
        {
            CompleteLine();
        }

        // Flush a possible high surrogate carried across Append calls using
        // the same replacement behavior as Encoding.UTF8.GetBytes(string).
        _utf8Encoder.Convert(
            ReadOnlySpan<char>.Empty,
            _encodingBuffer.AsSpan(),
            flush: true,
            out _,
            out var bytesWritten,
            out _);
        _sourceHash.AppendData(_encodingBuffer.AsSpan(0, bytesWritten));
        var sourceHash = Convert.ToHexString(_sourceHash.GetHashAndReset());

        var counts = new ReadOnlyDictionary<string, long>(
            new SortedDictionary<string, long>(_codeCounts, StringComparer.Ordinal));

        _report = new CompilerLogReport(
            _incomplete ? "incomplete" : _diagnosticLineCount > 0 ? "errors_found" : "no_recognized_errors",
            _diagnosticLineCount,
            counts,
            Array.AsReadOnly(_diagnostics.ToArray()),
            Array.AsReadOnly(_buildSummaries.ToArray()),
            _diagnosticLineCount - _diagnostics.Count,
            _unparsedLongLines,
            sourceHash)
        {
            OmittedBuildSummaries = _buildSummaryCount - _buildSummaries.Count
        };

        Dispose();
        return _report;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sourceHash.Dispose();
        _disposed = true;
    }

    private void AppendHash(ReadOnlySpan<char> characters)
    {
        while (!characters.IsEmpty)
        {
            _utf8Encoder.Convert(
                characters,
                _encodingBuffer.AsSpan(),
                flush: false,
                out var charactersRead,
                out var bytesWritten,
                out _);
            _sourceHash.AppendData(_encodingBuffer.AsSpan(0, bytesWritten));
            characters = characters[charactersRead..];
        }
    }

    private void CompleteLine()
    {
        if (!_discardingLongLine)
        {
            AnalyzeLine(_line.ToString());
        }

        _line.Clear();
        _discardingLongLine = false;
        _sourceLine++;
    }

    private void AnalyzeLine(string rawText)
    {
        var text = AnsiEscapePattern.Replace(rawText, string.Empty);
        var summary = BuildSummaryPattern.Match(text);
        if (summary.Success)
        {
            if (!long.TryParse(
                    summary.Groups["count"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var errorCount))
            {
                _incomplete = true;
                return;
            }

            _buildSummaryCount++;
            if (_buildSummaries.Count < MaximumSamples)
            {
                var sample = Sample(rawText, out var truncated);
                _buildSummaries.Add(new CompilerLogBuildSummary(_sourceLine, errorCount, sample)
                {
                    Truncated = truncated
                });
            }

            return;
        }

        var diagnostic = DiagnosticPattern.Match(text);
        if (diagnostic.Success)
        {
            if (!string.Equals(
                    diagnostic.Groups["severity"].Value,
                    "warning",
                    StringComparison.OrdinalIgnoreCase))
            {
                var code = diagnostic.Groups["code"].Success
                    ? diagnostic.Groups["code"].Value.ToUpperInvariant()
                    : "uncoded";
                if (!KnownCompilerCodePattern.IsMatch(code)
                    && !CompilerOriginPattern.IsMatch(text[..diagnostic.Groups["severity"].Index]))
                {
                    return;
                }

                _diagnosticLineCount++;
                _codeCounts.TryGetValue(code, out var count);
                _codeCounts[code] = count + 1;

                if (_diagnostics.Count < MaximumSamples)
                {
                    var sample = Sample(rawText, out var truncated);
                    _diagnostics.Add(new CompilerLogDiagnostic(_sourceLine, code, sample, truncated));
                }
            }

            return;
        }

    }

    private static string Sample(string rawText, out bool truncated)
    {
        truncated = rawText.Length > MaximumSampleCharacters;
        if (!truncated)
        {
            return rawText;
        }

        var length = MaximumSampleCharacters;
        if (char.IsHighSurrogate(rawText[length - 1]))
        {
            length--;
        }

        return rawText[..length];
    }

    private void ThrowIfUnavailable()
    {
        if (_report is not null)
        {
            throw new InvalidOperationException("The compiler log scan has already completed.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
