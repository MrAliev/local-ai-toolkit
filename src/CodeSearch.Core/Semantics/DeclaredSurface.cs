using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// Whether an edit to a file can change how code in another file binds.
///
/// It can only do that by changing what the file declares. A body is invisible from outside: no
/// name lookup, no overload resolution and no inheritance depends on it. So when the declared
/// surface is unchanged, every occurrence already recorded for every other file stays correct as
/// it is, and only the edited file needs recomputing -- which is what keeps a saved file from
/// reopening the solution (#519).
///
/// The judgement is equality rather than a classification of differences, deliberately. Adding a
/// member is not safe: a new overload or a new extension method can send an existing call site
/// somewhere else. A rule that tried to say which additions are harmless would be wrong in a way
/// nobody notices, and this one is wrong only in the direction that costs time.
///
/// Syntax only. No compilation, no references, no restore -- both versions of one file are
/// compared as written, which is all that is needed to tell a body edit from an API edit.
/// </summary>
public static class DeclaredSurface
{
    /// <summary>
    /// True only when both versions parse and declare exactly the same things. Source that does
    /// not parse has no surface anyone can rely on, so it is never reported unchanged -- not even
    /// against itself.
    /// </summary>
    public static bool Unchanged(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var first = Of(before);
        var second = Of(after);
        return first is not null &&
               second is not null &&
               string.Equals(first, second, StringComparison.Ordinal);
    }

    private static string? Of(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        if (tree.GetDiagnostics().Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var declarations = tree.GetRoot()
            .DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Select(Describe)
            .ToList();
        declarations.Sort(StringComparer.Ordinal);
        return string.Join(";", declarations);
    }

    /// <summary>
    /// One declaration, as the outside world sees it: where it sits, and its header up to the
    /// point where the body begins. Tokens rather than text, so comments and spacing are gone by
    /// construction; sorted by the caller, because member order is invisible in C#.
    /// </summary>
    private static string Describe(MemberDeclarationSyntax member)
    {
        var header = member switch
        {
            // A namespace is a member declaration too, and its span is the whole file. Left to
            // the fallback it swallowed every body in it, and a body edit read as an API edit.
            BaseNamespaceDeclarationSyntax space => Until(member, space.Name.Span.End),
            BaseTypeDeclarationSyntax type => Until(member, type.OpenBraceToken.SpanStart),
            EnumMemberDeclarationSyntax => Until(member, member.Span.End),
            BaseMethodDeclarationSyntax method => Until(
                member,
                method.Body?.SpanStart
                    ?? method.ExpressionBody?.SpanStart
                    ?? method.Span.End),
            PropertyDeclarationSyntax property => Until(
                member,
                property.AccessorList?.SpanStart
                    ?? property.ExpressionBody?.SpanStart
                    ?? property.Span.End),
            BaseFieldDeclarationSyntax field => Until(member, field.Declaration.Span.End),
            _ => Until(member, member.Span.End),
        };

        return Where(member) + " " + member.Kind() + " " + header;
    }

    private static string Until(SyntaxNode node, int end) =>
        string.Join(
            " ",
            node.DescendantTokens()
                .TakeWhile(token => token.SpanStart < end)
                .Select(token => token.Text));

    /// <summary>
    /// The names it is nested inside, so two members of the same name in different types are two
    /// declarations rather than one.
    /// </summary>
    private static string Where(SyntaxNode node)
    {
        var names = new List<string>();
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case BaseTypeDeclarationSyntax type:
                    names.Add(type.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax space:
                    names.Add(space.Name.ToString());
                    break;
            }
        }

        names.Reverse();
        return string.Join(".", names);
    }
}
