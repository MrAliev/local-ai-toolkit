using System.Reflection;
using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using ModelContextProtocol.Server;

namespace CodeSearch.Tests;

/// <summary>
/// One server, one contract for the repository root.
///
/// Every tool here resolves an omitted root to the repository containing the working
/// directory — except that the two LSP tools once required it, so a caller who omitted it
/// exactly as they may everywhere else got the MCP layer's bare "An error occurred invoking
/// 'lsp_open_document'." with nothing naming the missing parameter (#238). The rule is
/// checked by reflection rather than by remembering it, because the next tool added to this
/// class is the one that would break it again.
/// </summary>
public sealed class McpRootParameterTests
{
    [Fact]
    public void Every_tool_that_takes_a_root_lets_the_caller_leave_it_out()
    {
        var required = typeof(CodeSearchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method
                .GetParameters()
                .Where(parameter =>
                    parameter.Name == "root" &&
                    !parameter.IsOptional)
                .Select(_ => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(required);
    }

    /// <summary>
    /// An optional parameter whose description does not say what omitting it does is optional
    /// only to the compiler: the caller reading the tool list still cannot tell. The wording
    /// is left to each tool — search_code has a worktree caveat worth stating, get_code_chunk
    /// a snapshot one — but every one of them has to answer the question.
    /// </summary>
    [Fact]
    public void The_root_parameter_says_what_omitting_it_means()
    {
        var descriptions = typeof(CodeSearchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method
                .GetParameters()
                .Where(parameter => parameter.Name == "root")
                .Select(parameter => (
                    Tool: method.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
                    Description: parameter
                        .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()
                        ?.Description ?? string.Empty)))
            .ToArray();

        Assert.NotEmpty(descriptions);
        var silent = descriptions
            .Where(entry => !entry.Description.Contains(
                "Defaults to",
                StringComparison.Ordinal))
            .Select(entry => entry.Tool)
            .ToArray();

        Assert.True(
            silent.Length == 0,
            "These tools take an optional root without saying what omitting it does: " +
            string.Join(", ", silent));
    }

    /// <summary>
    /// The failure this fixes, from the caller's side: omitting the root has to produce an
    /// answer somebody can act on. On a default installation that answer is that live
    /// language servers are switched off — readable is the point, not successful.
    /// </summary>
    [Fact]
    public async Task Opening_a_document_without_a_root_answers_instead_of_faulting()
    {
        using var sessions = new DisabledSessions();

        var opened = await CodeSearchTools.LspOpenDocument(
            sessions.Manager,
            "src/Program.cs",
            "csharp",
            1,
            "class Program { }");
        var closed = await CodeSearchTools.LspCloseDocument(
            sessions.Manager,
            "src/Program.cs");

        Assert.Equal(
            "lsp_open_document failed: Live language-server integration is disabled.",
            opened);
        // Closing a document that was never opened is still not an error — idempotent close is
        // the design and stays. What changed is the sentence: it used to answer "LSP document
        // closed", claiming an effect that cannot have happened on an installation where opening
        // one is refused, which is the whole of what a caller reads.
        Assert.Equal("No live LSP document for src/Program.cs.", closed);
    }

    /// <summary>
    /// A session manager configured the way a default installation is: language servers are
    /// off, so asking for one says so rather than starting anything.
    /// </summary>
    /// <summary>
    /// And a document that really was open still closes, and still says so. The point of the
    /// change is that the two cases stopped reading identically, not that the sentence moved.
    /// </summary>
    [Fact]
    public async Task Closing_a_document_that_was_open_says_it_closed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codesearch-lsp-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var sessions = new LanguageServerSessionManager(
                (_, _) => new SilentClient());
            await sessions.OpenOrUpdateAsync(
                root,
                "Program.cs",
                "csharp",
                1,
                "class P { }",
                TestContext.Current.CancellationToken);

            var closed = await CodeSearchTools.LspCloseDocument(sessions, "Program.cs", root);

            Assert.Equal($"LSP document closed: Program.cs.", closed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A language server that accepts everything and answers nothing.</summary>
    private sealed class SilentClient : ILanguageServerClient
    {
        private static readonly Task<IReadOnlyList<LspLocation>> None =
            Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task InitializeAsync(string workspaceRoot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DidOpenAsync(LspTextDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DidChangeAsync(LspTextDocument document, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DidCloseAsync(Uri documentUri, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(
            Uri documentUri, int line, int utf16Column, CancellationToken cancellationToken) =>
            None;

        public Task<IReadOnlyList<LspLocation>> FindReferencesAsync(
            Uri documentUri,
            int line,
            int utf16Column,
            bool includeDefinition,
            CancellationToken cancellationToken) =>
            None;

        public Task<IReadOnlyList<LspLocation>> FindImplementationsAsync(
            Uri documentUri, int line, int utf16Column, CancellationToken cancellationToken) =>
            None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisabledSessions : IDisposable
    {
        public LanguageServerSessionManager Manager { get; } = new((_, _) =>
            throw new InvalidOperationException(
                "Live language-server integration is disabled."));

        public void Dispose() => Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
