// SqlTreeQueries.cs

using SpiritSync.Generators.Parser.SqlNodes;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Shared read-only queries over a <see cref="SqlNode"/> parse tree.
/// </summary>
/// <remarks>
/// Broken out from <see cref="SqlTransformer"/> and <see cref="SqlParserQuery"/> so both
/// wrappers can consult the tree without duplicating identifier-matching logic.
/// </remarks>
internal static class SqlTreeQueries
{
    /// <summary>
    /// Finds the CTE whose alias identifier matches <paramref name="cteAlias"/> (case-insensitive).
    /// </summary>
    public static SqlNode? FindCte(SqlNode root, string cteAlias, string sql)
    {
        var with = root.WithClause();
        if (with is null) return null;

        foreach (var cte in with.CommonTableExpressions())
        {
            var alias = cte.FirstMeaningfulElement();
            if (alias is { } e && e.IsIdentifierMatching(cteAlias, sql))
                return cte;
        }

        return null;
    }

    /// <summary>
    /// Extracts the alias of a LEFT OUTER JOIN from a line of elements.
    /// A LEFT OUTER JOIN line has the form: LEFT OUTER JOIN &lt;table-or-subquery&gt; [AS] &lt;alias&gt; ON ...
    /// For a subquery join (opening paren after JOIN), the alias follows the closing paren.
    /// Returns <c>null</c> if the alias cannot be determined.
    /// </summary>
    public static string? ExtractLeftOuterJoinAlias(List<SqlSyntaxElement> line, string source)
    {
        var i = 0;

        // Expect: LEFT OUTER JOIN
        line.SkipTrivia(ref i);
        if (i >= line.Count || !line[i].IsKeyword(SqlKeyword.Left)) return null;
        i++;
        line.SkipTrivia(ref i);
        if (i >= line.Count || !line[i].IsKeyword(SqlKeyword.Outer)) return null;
        i++;
        line.SkipTrivia(ref i);
        if (i >= line.Count || !line[i].IsKeyword(SqlKeyword.Join)) return null;
        i++;
        line.SkipTrivia(ref i);
        if (i >= line.Count) return null;

        // After JOIN: either a subquery (Node or OpenParen) or a plain/schema-qualified table name
        if (line[i].IsNode() || line[i].IsToken(SqlTokenKind.OpenParen))
        {
            // Subquery — skip past the node or balanced paren group, then find alias.
            if (line[i].IsNode())
                i++;
            else
                line.SkipBalancedParens(ref i);

            line.SkipTrivia(ref i);
            if (i >= line.Count) return null;
            line.SkipOptionalAs(ref i);
            if (i >= line.Count) return null;

            return line[i].IsToken(SqlTokenKind.Identifier) ? line[i].Token.ToString(source) : null;
        }

        // Table name identifier (maybe schema-qualified: schema.table alias)
        if (!line[i].IsToken(SqlTokenKind.Identifier)) return null;
        i++; // skip table name (or schema prefix)

        // Handle schema.table: skip dot and table name
        line.SkipTrivia(ref i);
        if (i < line.Count && line[i].IsToken(SqlTokenKind.Dot))
        {
            i++; // skip dot
            line.SkipTrivia(ref i);
            if (i < line.Count && line[i].IsToken(SqlTokenKind.Identifier))
                i++; // skip table name
        }

        line.SkipTrivia(ref i);
        if (i >= line.Count) return null;
        line.SkipOptionalAs(ref i);
        if (i >= line.Count) return null;

        return line[i].IsToken(SqlTokenKind.Identifier) ? line[i].Token.ToString(source) : null;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="alias"/> is referenced anywhere after the
    /// <c>WHERE</c> keyword in <paramref name="fromClause"/> as a qualifier (i.e. <c>alias.</c>).
    /// Only tokens after the first top-level WHERE keyword are examined, so ON-clause
    /// references inside JOIN definitions are not counted.
    /// </summary>
    public static bool IsAliasReferencedInWhere(SqlNode? fromClause, string alias, string source)
    {
        if (fromClause is null) return false;

        var seenWhere = false;
        SqlToken? prev = null;

        foreach (var tok in fromClause.AllTokens())
        {
            if (!seenWhere)
            {
                if (tok is { Kind: SqlTokenKind.Keyword, Keyword: SqlKeyword.Where })
                    seenWhere = true;
                continue;
            }

            if (tok.Kind == SqlTokenKind.Dot && prev is { Kind: SqlTokenKind.Identifier } &&
                IdentifierEquals(prev.Value.AsSpan(source), alias))
                return true;

            if (!tok.IsTrivia) prev = tok;
        }

        return false;
    }

    /// <summary>
    /// Enumerates all tokens from a flat list of <see cref="SqlSyntaxElement"/>s,
    /// recursing into child nodes.
    /// </summary>
    public static IEnumerable<SqlToken> EnumerateAllTokens(List<SqlSyntaxElement> elements) =>
        elements.AllTokens();

    /// <summary>Case-insensitive comparison of a bracketed/quoted-or-plain identifier span to <paramref name="expected"/>.</summary>
    public static bool IdentifierEquals(ReadOnlySpan<char> span, string expected)
    {
        if (span.Length >= 2)
        {
            if (span[0] == '[' && span[^1] == ']')
                span = span.Slice(1, span.Length - 2);
            else if (span[0] == '"' && span[^1] == '"')
                span = span.Slice(1, span.Length - 2);
        }

        if (span.Length != expected.Length) return false;

        for (var i = 0; i < span.Length; i++)
        {
            var a = span[i]; var b = expected[i];
            var au = a is >= 'a' and <= 'z' ? (char)(a - 32) : a;
            var bu = b is >= 'a' and <= 'z' ? (char)(b - 32) : b;
            if (au != bu) return false;
        }

        return true;
    }
}
