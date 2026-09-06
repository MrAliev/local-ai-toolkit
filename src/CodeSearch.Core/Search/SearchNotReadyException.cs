namespace CodeSearch.Core.Search;

/// <summary>
/// The refusal a search gives rather than answering from an index that does not match the tree
/// in front of it: no overlay for this worktree's state, a generation behind what is asked for,
/// a model or dimension that does not match. It is a fact about what has been built, not a
/// fault, and the caller's remedy is to build the overlay.
///
/// It used to live in <c>SearchReadinessGate.cs</c>, beside a readiness gate nothing called.
/// The gate is gone (#394); this is the part of that file the product actually uses —
/// <c>SearchService</c>, <c>SearchQualityProfile</c> and <c>ConsoleJson</c> all name it.
/// </summary>
public sealed class SearchNotReadyException(string message)
    : InvalidOperationException(message);
