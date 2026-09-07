using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

/// <summary>
/// Editing two files out of 715 costs 49.67 seconds, of which 36.72 is opening a solution of 26
/// projects, analysing it and running the SCIP adapters. A repeated sync on an unchanged tree
/// costs 1.33. The whole difference is that any save changes the dirty hash, the semantic overlay
/// is keyed by it, and so the phase runs again from nothing (#385).
///
/// The measured remedy is not a parse cache — producing semantics for one changed C# file still
/// needs the solution opened, and opening it is the cost. It is to notice that **for most edits
/// the previous semantic overlay is still true**, and this is the line that decides which edits
/// those are.
///
/// The boundary is the owner's, and it is the strict one: an edit is semantic unless it is
/// plainly not. Being wrong in the safe direction costs 36 seconds; being wrong in the other
/// direction serves navigation from a stale overlay, which is the one thing the index promises
/// never to do.
/// </summary>
public sealed class WhatCountsAsASemanticChangeTests
{
    [Theory]
    // The languages the semantic layer actually reads.
    [InlineData("src/CodeSearch.Core/Indexing/IndexBuilder.cs")]
    [InlineData("src/LocalAi.Installer/MainWindow.xaml")]
    [InlineData("web/app.ts")]
    [InlineData("web/app.tsx")]
    [InlineData("web/app.js")]
    [InlineData("web/app.jsx")]
    [InlineData("web/app.mjs")]
    [InlineData("web/app.cjs")]
    [InlineData("tools/report.py")]
    // The Python adapter is fed `.py` and `.pyi` alike, so a stub is source the layer reads.
    // Left out of the boundary, editing one leaves the overlay declared current and navigation
    // answered from an overlay built before the stub changed — the unsound direction.
    [InlineData("tools/report.pyi")]
    // What the solution is made of. None of these is read by the semantic layer as source, and
    // every one of them changes what the layer would find: a project reference added, a target
    // framework changed, a file glob widened.
    [InlineData("src/CodeSearch.Core/CodeSearch.Core.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("LocalAi.slnx")]
    [InlineData("web/tsconfig.json")]
    [InlineData("web/package.json")]
    public void A_change_the_semantic_layer_would_see_counts(string path) =>
        Assert.True(SemanticCorpusPolicy.ChangesSemantics(path), path);

    [Theory]
    // The common edit, and the one this exists for: prose, resources, data.
    [InlineData("README.md")]
    [InlineData("CLAUDE.ru.md")]
    [InlineData("src/CodeSearch.Core/Resources/IndexText.resx")]
    [InlineData("src/CodeSearch.Core/Resources/IndexText.ru.resx")]
    [InlineData("docs/overview.svg")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData("data/seed.sql")]
    [InlineData("notes.txt")]
    // A JSON file that is not one of the two the build reads is data like any other.
    [InlineData("src/LocalAi.Cli/catalog.json")]
    public void A_change_it_could_not_see_does_not(string path) =>
        Assert.False(SemanticCorpusPolicy.ChangesSemantics(path), path);

    /// <summary>
    /// Windows hands paths back with backslashes and Git with forward ones, and the same file has
    /// to answer the same way whichever arrives — this predicate decides whether a build costing
    /// half a minute runs.
    /// </summary>
    [Theory]
    [InlineData("src\\CodeSearch.Core\\Indexing\\IndexBuilder.cs", true)]
    [InlineData("web\\tsconfig.json", true)]
    [InlineData("docs\\overview.md", false)]
    public void The_separator_does_not_change_the_answer(string path, bool semantic) =>
        Assert.Equal(semantic, SemanticCorpusPolicy.ChangesSemantics(path));

    /// <summary>
    /// The TypeScript list has one home. It was in <c>CodeSearchSyncCommand</c>, where it chose
    /// what to feed scip-typescript; the semantic boundary needs the same list, and two copies of
    /// one list is how the boundary comes to disagree with what the adapter actually read.
    /// </summary>
    [Theory]
    [InlineData("app.ts", true)]
    [InlineData("app.mjs", true)]
    [InlineData("app.py", false)]
    [InlineData("app.cs", false)]
    public void The_TypeScript_family_is_one_list(string path, bool typescript) =>
        Assert.Equal(typescript, SemanticCorpusPolicy.IsTypeScriptSource(path));
}
