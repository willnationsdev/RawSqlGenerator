// SqlParserQuery.cs

namespace SpiritSync.Generators.Parser;

/// <summary>
/// A read-only inspection wrapper over a <see cref="SqlParser"/>'s parse tree, exposing
/// utility queries that do not modify the SQL.
/// </summary>
/// <remarks>
/// Obtained via <see cref="SqlParser.Query"/>. All queries reuse the parser's cached
/// <see cref="SqlParser.Root"/> tree, so calling multiple queries on the same parser
/// never reparses the source.
/// </remarks>
internal readonly struct SqlParserQuery
{
    private readonly SqlParser _parser;

    internal SqlParserQuery(SqlParser parser) { _parser = parser; }

    /// <summary>Returns true if the named CTE contains a top-level <c>ORDER BY</c> clause.</summary>
    public bool CteHasOrderBy(string cteAlias)
    {
        var cte = SqlTreeQueries.FindCte(_parser.Root, cteAlias, _parser.Source);
        var body = cte?.FirstDescendant(SqlSyntaxKind.CteBody);
        var stmt = body?.FirstDescendant(SqlSyntaxKind.Query);
        return stmt?.FirstDescendant(SqlSyntaxKind.OrderByClause) is not null;
    }

    /// <summary>
    /// Scans the source for unresolved <c>{...}</c> interpolation placeholders that appear
    /// outside of SQL string literals, yielding their 1-based line, 1-based column, and
    /// trimmed inner content.
    /// </summary>
    /// <remarks>
    /// Reuses the parser's cached token stream via the <see cref="SqlParser.Root"/> tree —
    /// no additional tokenization pass is performed.
    /// </remarks>
    public IEnumerable<(int Line, int Column, string Content)> FindUnresolvedInterpolations() =>
        SqlText.FindUnresolvedInterpolations(_parser.Source);
}
