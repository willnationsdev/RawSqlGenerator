// SqlSyntaxExtensions.cs

using SpiritSync.Generators.Parser.SqlNodes;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Domain-specific extension methods for navigating and querying SQL parse-tree nodes,
/// syntax elements, and tokens. Centralises low-level predicate and traversal logic so
/// that higher-level code (e.g. <see cref="SqlTreeQueries"/>) can be written in terms of
/// intent rather than structural detail.
/// </summary>
internal static class SqlSyntaxExtensions
{
    // -------------------------------------------------------------------------
    // SqlSyntaxElement predicates
    // -------------------------------------------------------------------------

    /// <summary>Returns <c>true</c> if the element is a trivia token (whitespace / comment).</summary>
    public static bool IsTrivia(this SqlSyntaxElement e) =>
        e is { Kind: SqlElementKind.Token, Token.IsTrivia: true };

    /// <summary>Returns <c>true</c> if the element is a token whose keyword matches <paramref name="kw"/>.</summary>
    public static bool IsKeyword(this SqlSyntaxElement e, SqlKeyword kw) =>
        e.Kind == SqlElementKind.Token && e.Token.IsKeyword(kw);

    /// <summary>Returns <c>true</c> if the element is a token of the given <paramref name="kind"/>.</summary>
    public static bool IsToken(this SqlSyntaxElement e, SqlTokenKind kind) =>
        e.Kind == SqlElementKind.Token && e.Token.Kind == kind;

    /// <summary>Returns <c>true</c> if the element is a child node (not a token or text).</summary>
    public static bool IsNode(this SqlSyntaxElement e) =>
        e.Kind == SqlElementKind.Node;

    /// <summary>
    /// Returns <c>true</c> if the element is an identifier token whose text matches
    /// <paramref name="expected"/> (case-insensitive, bracket/quote-stripped).
    /// </summary>
    public static bool IsIdentifierMatching(this SqlSyntaxElement e, string expected, string source) =>
        e.Kind == SqlElementKind.Token &&
        e.Token.Kind == SqlTokenKind.Identifier &&
        SqlTreeQueries.IdentifierEquals(e.Token.AsSpan(source), expected);

    // -------------------------------------------------------------------------
    // SqlToken predicates
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> if the token is an identifier whose text matches
    /// <paramref name="expected"/> (case-insensitive, bracket/quote-stripped).
    /// </summary>
    public static bool IsIdentifierMatching(this SqlToken token, string expected, string source) =>
        token.Kind == SqlTokenKind.Identifier &&
        SqlTreeQueries.IdentifierEquals(token.AsSpan(source), expected);

    // -------------------------------------------------------------------------
    // List<SqlSyntaxElement> cursor helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Advances <paramref name="i"/> past any trivia elements, stopping at the first
    /// non-trivia element or the end of the list.
    /// </summary>
    public static void SkipTrivia(this List<SqlSyntaxElement> elements, ref int i)
    {
        while (i < elements.Count && elements[i].IsTrivia()) i++;
    }

    /// <summary>
    /// Advances <paramref name="i"/> past a balanced parenthesised group in a flat token
    /// list, starting at the opening <c>(</c>. On return <paramref name="i"/> points to
    /// the element immediately after the matching <c>)</c>.
    /// </summary>
    public static void SkipBalancedParens(this List<SqlSyntaxElement> elements, ref int i)
    {
        var depth = 0;
        while (i < elements.Count)
        {
            var e = elements[i];
            i++;
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.OpenParen) depth++;
                else if (e.Token.Kind == SqlTokenKind.CloseParen)
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
        }
    }

    /// <summary>
    /// If the element at <paramref name="i"/> is an <c>AS</c> keyword, advances
    /// <paramref name="i"/> past it and any following trivia.
    /// </summary>
    public static void SkipOptionalAs(this List<SqlSyntaxElement> elements, ref int i)
    {
        if (i < elements.Count && elements[i].IsKeyword(SqlKeyword.As))
        {
            i++;
            elements.SkipTrivia(ref i);
        }
    }

    // -------------------------------------------------------------------------
    // SqlNode traversal helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enumerates every token in the node tree (depth-first), including trivia.
    /// </summary>
    public static IEnumerable<SqlToken> AllTokens(this SqlNode node)
    {
        foreach (var e in node.Elements)
        {
            if (e.Kind == SqlElementKind.Token) yield return e.Token;
            else if (e.Kind == SqlElementKind.Node)
                foreach (var t in e.Node!.AllTokens()) yield return t;
        }
    }

    /// <summary>
    /// Enumerates every token reachable from a flat element list (depth-first),
    /// including trivia.
    /// </summary>
    public static IEnumerable<SqlToken> AllTokens(this List<SqlSyntaxElement> elements)
    {
        foreach (var e in elements)
        {
            if (e.Kind == SqlElementKind.Token) yield return e.Token;
            else if (e.Kind == SqlElementKind.Node)
                foreach (var t in e.Node!.AllTokens()) yield return t;
        }
    }

    /// <summary>
    /// Returns the <c>WITH</c> clause node that is a descendant of <paramref name="root"/>,
    /// or <c>null</c> if the query has no CTEs.
    /// </summary>
    public static SqlNode? WithClause(this SqlNode root) =>
        root.FirstDescendant(SqlSyntaxKind.WithClause);

    /// <summary>
    /// Enumerates the <c>CommonTableExpression</c> child nodes of a <c>WITH</c> clause node.
    /// </summary>
    public static IEnumerable<SqlNode> CommonTableExpressions(this SqlNode withClause) =>
        withClause.ChildNodes(SqlSyntaxKind.CommonTableExpression);

    /// <summary>
    /// Returns the first non-trivia token element in a CTE node's element list, or
    /// <c>null</c> if none exists. This is the alias identifier of the CTE.
    /// </summary>
    public static SqlSyntaxElement? FirstMeaningfulElement(this SqlNode node)
    {
        foreach (var e in node.Elements)
        {
            if (e.Kind != SqlElementKind.Token) return null;
            if (e.Token.IsTrivia) continue;
            return e;
        }
        return null;
    }
}
