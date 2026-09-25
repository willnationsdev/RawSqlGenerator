// SqlSyntaxKind.cs

using SpiritSync.Generators.Parser.SqlNodes;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// The kind of <see cref="SqlNode"/> in the parse tree produced by <see cref="SqlParser"/>.
/// </summary>
internal enum SqlSyntaxKind : byte
{
    /// <summary>The root node: a sequence of top-level statements plus surrounding trivia.</summary>
    Script,

    /// <summary>A <c>WITH</c> clause containing one or more CTEs. Precedes a top-level SELECT.</summary>
    WithClause,

    /// <summary>A single common table expression: <c>alias AS (body)</c>.</summary>
    CommonTableExpression,

    /// <summary>The body of a CTE — everything between the outer parentheses.</summary>
    CteBody,

    /// <summary>A query containing <c>SELECT ... FROM ... [ORDER BY ...] ...</c> clauses.</summary>
    Query,

    /// <summary>The <c>SELECT column-list</c> clause of a select statement.</summary>
    SelectClause,

    /// <summary>The <c>FROM</c> clause and everything following it that is not another top-level clause we recognize.</summary>
    FromClause,

    /// <summary>The <c>ORDER BY</c> clause of a select statement (including OFFSET/FETCH if present).</summary>
    OrderByClause,

    /// <summary>
    /// A comma-separated list of column expressions accompanying a <c>SELECT</c>,
    /// <c>ORDER BY</c>, <c>GROUP BY</c>, or similar clause. Each direct child element is
    /// either a column expression (token run or sub-node) or a comma/whitespace separator.
    /// </summary>
    ColumnList,

    /// <summary>A raw, unstructured run of tokens that the parser did not further decompose.</summary>
    RawTokens,

    /// <summary>A single column expression within a <c>SELECT</c> or <c>ORDER BY</c> column list, optionally including an <c>AS alias</c>.</summary>
    ColumnExpression,

    /// <summary>The primary (non-joined) table or subquery in a <c>FROM</c> clause, with optional alias.</summary>
    TableRelation,

    /// <summary>A single <c>JOIN</c> clause within a <c>FROM</c> clause, including the join type, target, alias, and <c>ON</c> condition.</summary>
    JoinClause,

    /// <summary>A <c>CASE WHEN … THEN … [ELSE …] END</c> expression parsed from a column expression.</summary>
    CaseExpression,

    /// <summary>A single <c>WHEN predicate THEN result</c> branch within a <see cref="CaseExpression"/>.</summary>
    CaseWhenExpression,

    /// <summary>The <c>ELSE result</c> branch within a <see cref="CaseExpression"/>, if present.</summary>
    CaseElseExpression,

    /// <summary>
    /// A predicate expression (the condition in a <c>WHEN</c> clause or similar).
    /// May contain <c>AND</c>/<c>OR</c> chains and <c>EXISTS(…)</c> sub-queries.
    /// </summary>
    Predicate,
}
