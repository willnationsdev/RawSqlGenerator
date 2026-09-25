// SqlTransformer.cs

using System.Text;
using SpiritSync.Generators.Parser.SqlNodes;

// ReSharper disable ForCanBeConvertedToForeach

namespace SpiritSync.Generators.Parser;

/// <summary>
/// A mutable transformation wrapper around a <see cref="SqlParser"/>'s parse tree.
/// </summary>
/// <remarks>
/// <para>
/// Obtained via <see cref="SqlParser.Transform"/>. Each transformer holds a private
/// <em>clone</em> of the parser's cached parse tree, so mutations never affect the
/// originating <see cref="SqlParser"/>. Multiple mutation methods can be chained on the
/// same transformer without reparsing the source.
/// </para>
/// <para>
/// Call <see cref="Build"/> when you need a fresh <see cref="SqlParser"/> whose
/// <see cref="SqlParser.Source"/> reflects all applied edits. That is the only place
/// this API reparses text.
/// </para>
/// </remarks>
internal sealed class SqlTransformer
{
    private readonly SqlParser _parser;
    private SqlNode? _tree;

    internal SqlTransformer(SqlParser parser) { _parser = parser; }

    /// <summary>The (lazily cloned) working parse tree that all mutations apply to.</summary>
    private SqlNode Tree => _tree ??= _parser.Root.Clone();

    /// <summary>The source string that the working tree's token slices refer into.</summary>
    private string Source => _parser.Source;

    /// <summary>
    /// Replaces the column list of the top-level <c>SELECT</c> with
    /// <paramref name="idsColumnExpression"/>, removes the <c>ORDER BY</c> clause
    /// (including any OFFSET/FETCH), and strips any LEFT OUTER JOINs whose alias is
    /// not referenced in the WHERE clause.
    /// </summary>
    /// <returns>The same transformer instance for fluent chaining.</returns>
    public SqlTransformer ReplaceSelectColumns(string idsColumnExpression)
    {
        var stmt = OuterSelectStatement(Tree) ?? Tree.FirstDescendant(SqlSyntaxKind.Query);
        var select = stmt?.FirstDescendant(SqlSyntaxKind.SelectClause)
                     ?? Tree.FirstDescendant(SqlSyntaxKind.SelectClause);
        if (select is not null)
            ReplaceSelectClauseColumns(select, idsColumnExpression);

        if (stmt is not null)
        {
            // Remove ORDER BY (includes OFFSET/FETCH)
            var orderBy = stmt.FirstDescendant(SqlSyntaxKind.OrderByClause);
            if (orderBy is not null)
            {
                var idx = stmt.Elements.FindIndex(e => e.Kind == SqlElementKind.Node && ReferenceEquals(e.Node, orderBy));
                if (idx >= 0)
                {
                    stmt.Elements.RemoveAt(idx);
                    if (idx > 0)
                    {
                        var prev = stmt.Elements[idx - 1];
                        if (prev.Kind == SqlElementKind.Node && prev.Node!.Kind == SqlSyntaxKind.FromClause)
                            TrimTrailingTrivia(prev.Node!);
                    }
                }
            }

            // Remove LEFT OUTER JOIN statements whose alias is unreferenced in the corresponding WHERE clause (if any).
            // The FROM clause node contains everything from "FROM" through to (but not including) "ORDER BY",
            // including the WHERE clause tokens, so it serves as the scope for alias reference checks.
            var fromClause = stmt.FirstDescendant(SqlSyntaxKind.FromClause);
            if (fromClause is not null)
                RemoveUnreferencedJoinLines(fromClause, Source, whereClause: fromClause);
        }

        return this;
    }

    /// <summary>
    /// Rewrites the named CTE to select only <paramref name="idsColumnExpression"/> (adjusting
    /// or removing its ORDER BY), simplifies the outer <c>SELECT</c> to <c>alias.*</c>, and
    /// strips any <c>LEFT OUTER JOIN</c> / <c>OUTER APPLY</c> lines from the outer <c>FROM</c>.
    /// </summary>
    /// <returns>The same transformer instance for fluent chaining.</returns>
    public SqlTransformer ReplaceCteSqlColumns(string cteAlias, string idsColumnExpression, string? idsOrderByExpression = null)
    {
        var cte = SqlTreeQueries.FindCte(Tree, cteAlias, Source);
        if (cte is null)
        {
            var outerSelect = Tree.FirstDescendant(SqlSyntaxKind.SelectClause);
            if (outerSelect is not null) ReplaceSelectClauseColumns(outerSelect, idsColumnExpression);
            return this;
        }

        var cteBody = cte.FirstDescendant(SqlSyntaxKind.CteBody);
        var cteSelect = cteBody?.FirstDescendant(SqlSyntaxKind.SelectClause);
        if (cteSelect is not null) ReplaceSelectClauseColumns(cteSelect, idsColumnExpression);

        var cteStmt = cteBody?.FirstDescendant(SqlSyntaxKind.Query);
        var cteOrderBy = cteStmt?.FirstDescendant(SqlSyntaxKind.OrderByClause);
        var cteFrom = cteStmt?.FirstDescendant(SqlSyntaxKind.FromClause);
        if (cteOrderBy is not null)
            RewriteOrderBy(cteStmt!, cteOrderBy, idsOrderByExpression, Source, cteFrom is not null ? GetNodeLineIndent(cteStmt!, cteFrom) : null);

        var query = OuterSelectStatement(Tree);
        if (query is not null)
        {
            var outerSelectClause = query.ChildNodes(SqlSyntaxKind.SelectClause).FirstOrDefault();
            var outerFrom = query.ChildNodes(SqlSyntaxKind.FromClause).FirstOrDefault();
            if (outerSelectClause is not null && outerFrom is not null)
            {
                var alias = GetTableAliasInFromClause(outerFrom, cteAlias, Source) ?? cteAlias;
                var outerStmtIndent = GetNodeLineIndent(Tree, query);
                SimplifyOuterSelectToWildcard(outerSelectClause, outerFrom, alias, Source, query, outerStmtIndent);
                RemoveOptionalJoinLines(outerFrom, Source);
            }
        }

        return this;
    }

    /// <summary>Renders the mutated tree to text.</summary>
    private string ToSql() => (_tree ?? _parser.Root).ToString(Source);

    /// <summary>
    /// Produces a fresh <see cref="SqlParser"/> whose <see cref="SqlParser.Source"/> is the
    /// serialized result of all applied transformations. This is the only place the API
    /// reparses text; subsequent queries and transformations on the returned parser
    /// reuse its cached tree.
    /// </summary>
    public SqlParser Build() => new(ToSql(), _parser.Formatter);

    // -----------------------------------------------------------------------
    // Internal building blocks
    // -----------------------------------------------------------------------

    private static void ReplaceSelectClauseColumns(SqlNode selectClause, string expression)
    {
        // Locate the SqlColumnListNode child (produced by the granular parser) if present.
        // If found, we replace just that child element; otherwise fall back to the legacy
        // flat-token approach for trees that were built by an older parser version.
        var columnListIdx = -1;
        for (var i = 0; i < selectClause.Elements.Count; i++)
        {
            var e = selectClause.Elements[i];
            if (e.Kind == SqlElementKind.Node && e.Node!.Kind == SqlSyntaxKind.ColumnList)
            {
                columnListIdx = i;
                break;
            }
        }

        if (columnListIdx >= 0)
        {
            // Granular tree: extract column indent from the ColumnList node's leading trivia,
            // and trailing indent from the ColumnList node's trailing trivia.
            var columnListNode = selectClause.Elements[columnListIdx].Node!;
            var columnIndent = ExtractLeadingColumnIndent(columnListNode.Elements);
            var trailingIndent = ExtractTrailingLineIndent(columnListNode.Elements);

            // Replace the ColumnListNode element with a Text element containing the new expression.
            selectClause.Elements.RemoveAt(columnListIdx);
            if (columnIndent is { Length: > 0 })
            {
                var trailing = trailingIndent ?? columnIndent;
                selectClause.Elements.Insert(columnListIdx, SqlSyntaxElement.FromText("\n" + columnIndent + expression + "\n" + trailing));
            }
            else
            {
                selectClause.Elements.Insert(columnListIdx, SqlSyntaxElement.FromText(" " + expression + "\n"));
            }
            return;
        }

        // Legacy flat-token path (tree built without ColumnList node).
        var newElements = new List<SqlSyntaxElement>(4);
        var seenSelect = false;
        string? legacyColumnIndent = null;
        foreach (var e in selectClause.Elements)
        {
            if (!seenSelect)
            {
                newElements.Add(e);
                if (e.Kind == SqlElementKind.Token && e.Token.IsKeyword(SqlKeyword.Select))
                    seenSelect = true;
            }
            else
            {
                // Scan the trivia after SELECT to find the indentation of the first column.
                // Pattern: NewLine resets the accumulator; Whitespace accumulates; the first non-trivia
                // token ends the scan. We capture the whitespace on the last line before the first column.
                if (e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.NewLine })
                {
                    legacyColumnIndent = string.Empty; // reset on each newline
                }
                else if (e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.Whitespace })
                {
                    legacyColumnIndent = (legacyColumnIndent ?? string.Empty) + new string(' ', e.Token.Length);
                }
                else if (e.Kind == SqlElementKind.Text)
                {
                    // Text elements may contain newlines; find the last line's leading spaces.
                    var text = e.Text!;
                    var lastNl = text.LastIndexOf('\n');
                    if (lastNl >= 0)
                    {
                        var afterNl = text.Substring(lastNl + 1);
                        legacyColumnIndent = new string(' ', afterNl.Length - afterNl.TrimStart().Length);
                    }
                    break;
                }
                else
                {
                    // First non-trivia, non-text element — column indent is fully captured.
                    break;
                }
            }
        }

        // The trailing whitespace before FROM is the last whitespace run after the final newline
        // in the SelectClause. Capture it so we can restore it after the replacement expression,
        // keeping the FROM keyword at its original column position.
        var legacyTrailingIndent = ExtractTrailingLineIndent(selectClause.Elements);

        selectClause.Elements.Clear();
        selectClause.Elements.AddRange(newElements);

        if (legacyColumnIndent is { Length: > 0 })
        {
            var trailing = legacyTrailingIndent ?? legacyColumnIndent;
            selectClause.Add("\n" + legacyColumnIndent + expression + "\n" + trailing);
        }
        else
        {
            selectClause.Add(" " + expression + "\n");
        }
    }

    /// <summary>
    /// Scans the leading elements of a <see cref="SqlSyntaxKind.ColumnList"/> node to find
    /// the indentation of the first column expression (whitespace on the last line before the
    /// first non-trivia content).
    /// </summary>
    private static string? ExtractLeadingColumnIndent(List<SqlSyntaxElement> elements)
    {
        string? columnIndent = null;
        foreach (var e in elements)
        {
            if (e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.NewLine })
            {
                columnIndent = string.Empty;
            }
            else if (e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.Whitespace })
            {
                columnIndent = (columnIndent ?? string.Empty) + new string(' ', e.Token.Length);
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                var text = e.Text!;
                var lastNl = text.LastIndexOf('\n');
                if (lastNl >= 0)
                {
                    var afterNl = text.Substring(lastNl + 1);
                    columnIndent = new string(' ', afterNl.Length - afterNl.TrimStart().Length);
                }
                break;
            }
            else
            {
                break;
            }
        }
        return columnIndent;
    }

    /// <summary>
    /// Returns the whitespace on the last line of the element list — i.e. the characters
    /// between the final newline and the end of the list. Returns <c>null</c> if no newline
    /// is found or the last line contains non-whitespace content.
    /// </summary>
    private static string? ExtractTrailingLineIndent(List<SqlSyntaxElement> elements)
    {
        var sb = new StringBuilder();
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            var e = elements[i];
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine)
                {
                    // Reverse the accumulated chars to get left-to-right order.
                    var chars = sb.ToString().ToCharArray();
                    Array.Reverse(chars);
                    return new string(chars);
                }
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                {
                    for (var k = 0; k < e.Token.Length; k++) sb.Append(' ');
                }
                else
                {
                    return null; // non-whitespace before newline — no clean trailing indent
                }
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                var text = e.Text!;
                var lastNl = text.LastIndexOf('\n');
                if (lastNl >= 0)
                {
                    var afterNl = text.Substring(lastNl + 1);
                    if (afterNl.Length > 0 && afterNl.Trim().Length == 0)
                        return afterNl;
                    return null;
                }
                // No newline in this text element — keep scanning backwards.
                if (text.Trim().Length > 0) return null;
                sb.Append(new string(' ', text.Length - text.TrimStart().Length));
            }
            else
            {
                return null;
            }
        }
        return null;
    }

    private static SqlNode? OuterSelectStatement(SqlNode root)
    {
        foreach (var e in root.Elements)
        {
            if (e.Kind == SqlElementKind.Node && e.Node!.Kind == SqlSyntaxKind.Query)
                return e.Node;
        }
        return null;
    }

    private static void RewriteOrderBy(SqlNode owningStatement, SqlNode orderByClause, string? idsOrderByExpression, string source, string? bodyIndent = null)
    {
        var idx = owningStatement.Elements.FindIndex(e => e.Kind == SqlElementKind.Node && ReferenceEquals(e.Node, orderByClause));
        if (idx < 0) return;

        if (idsOrderByExpression is null)
        {
            owningStatement.Elements.RemoveAt(idx);
            if (idx > 0)
            {
                var prev = owningStatement.Elements[idx - 1];
                if (prev.Kind == SqlElementKind.Node && prev.Node!.Kind == SqlSyntaxKind.FromClause)
                    TrimTrailingTrivia(prev.Node!);
            }
            return;
        }

        var leadingWs = GetOrderByLeadingWhitespace(orderByClause, owningStatement);
        // Use the CTE body indent (FROM/WHERE level) if provided; otherwise fall back to the
        // original ORDER BY leading whitespace.
        var orderIndent = bodyIndent ?? leadingWs;

        // Capture the trailing whitespace from the original ORDER BY clause — the whitespace
        // on the last line (after the final newline), which precedes the closing ')' of the CTE.
        // ExtractTrailingLineIndent only works when the last tokens are whitespace; here the clause
        // ends with OFFSET...ONLY followed by a newline+indent, so we serialize and scan instead.
        var orderByText = orderByClause.ToString(source);
        var lastNlInOrderBy = orderByText.LastIndexOf('\n');
        var trailingWs = lastNlInOrderBy >= 0
            ? orderByText.Substring(lastNlInOrderBy + 1)
            : string.Empty;

        var replacement = new SqlNode(SqlSyntaxKind.OrderByClause);

        // Emit the leading newline + indent before ORDER so it aligns with FROM/WHERE.
        replacement.Add("\n" + orderIndent);

        // Skip the original leading trivia (newline + whitespace before ORDER) — we already
        // emitted the correct indent above. Once we hit ORDER, stop skipping trivia.
        var seenOrder = false;
        var seenBy = false;
        foreach (var e in orderByClause.Elements)
        {
            if (!seenOrder && e.Kind == SqlElementKind.Token &&
                (e.Token.Kind == SqlTokenKind.NewLine || e.Token.Kind == SqlTokenKind.Whitespace))
                continue;

            replacement.Elements.Add(e);
            if (!seenOrder && e.Kind == SqlElementKind.Token && e.Token.IsKeyword(SqlKeyword.Order))
            {
                seenOrder = true;
                continue;
            }
            if (seenOrder && e.Kind == SqlElementKind.Token && e.Token.IsKeyword(SqlKeyword.By))
            {
                seenBy = true;
                break;
            }
        }

        var parts = idsOrderByExpression.Split(',');
        var continuationIndent = orderIndent + new string(' ', SqlFormatter.OrderByPrefix.Length);
        if (!seenBy)
        {
            replacement.Add(" BY");
        }
        replacement.Add(" " + parts[0].Trim());
        for (var i = 1; i < parts.Length; i++)
        {
            replacement.Add("\n");
            replacement.Add(continuationIndent);
            replacement.Add(parts[i].Trim());
        }
        // Re-emit trailing whitespace so the closing ')' stays at its original column.
        replacement.Add("\n" + trailingWs);

        owningStatement.Elements[idx] = SqlSyntaxElement.FromNode(replacement);
    }

    private static string GetOrderByLeadingWhitespace(SqlNode orderBy, SqlNode owner)
    {
        var idx = owner.Elements.FindIndex(e => e.Kind == SqlElementKind.Node && ReferenceEquals(e.Node, orderBy));
        var sb = new StringBuilder();
        if (idx <= 0) return string.Empty;

        var stack = new Stack<char>();
        WalkBackForWhitespace(owner, idx - 1, stack);
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static void WalkBackForWhitespace(SqlNode owner, int idx, Stack<char> stack)
    {
        for (var i = idx; i >= 0; i--)
        {
            var e = owner.Elements[i];
            if (e.Kind == SqlElementKind.Node)
            {
                if (WalkBackNode(e.Node!, stack)) return;
            }
            else if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine) return;
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                {
                    for (var k = 0; k < e.Token.Length; k++) stack.Push(' ');
                }
                else
                {
                    return;
                }
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                var text = e.Text!;
                for (var k = text.Length - 1; k >= 0; k--)
                {
                    var c = text[k];
                    if (c == '\n') return;
                    if (c == ' ' || c == '\t') stack.Push(c);
                    else return;
                }
            }
        }
    }

    private static bool WalkBackNode(SqlNode node, Stack<char> stack)
    {
        for (var i = node.Elements.Count - 1; i >= 0; i--)
        {
            var e = node.Elements[i];
            if (e.Kind == SqlElementKind.Node)
            {
                if (WalkBackNode(e.Node!, stack)) return true;
            }
            else if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine) return true;
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                    for (var k = 0; k < e.Token.Length; k++) stack.Push(' ');
                else return true;
            }
            else
            {
                var text = e.Text!;
                for (var k = text.Length - 1; k >= 0; k--)
                {
                    var c = text[k];
                    if (c == '\n') return true;
                    if (c == ' ' || c == '\t') stack.Push(c);
                    else return true;
                }
            }
        }
        return false;
    }

    private static void SimplifyOuterSelectToWildcard(SqlNode selectClause, SqlNode fromClause, string alias, string sql, SqlNode? owningStatement = null, string? stmtIndent = null)
    {
        // Use the outer statement's own line indent (its position relative to the root) as the
        // base for SELECT/FROM alignment. This ensures that when the CTE list terminates (no
        // trailing comma), the outer SELECT aligns with the root WITH keyword rather than
        // inheriting the CTE body's deeper indentation.
        // Fall back to the FROM node's indent within the owning statement, or the FROM token's
        // own leading whitespace, in that order.
        var fromLeadingWs = stmtIndent
            ?? (owningStatement is not null
                ? GetNodeLineIndent(owningStatement, fromClause)
                : GetLeadingWhitespaceOfFirstSignificantToken(fromClause, sql));

        var current = GetSelectColumnsText(selectClause, sql).Trim();
        var simpleWildcard = alias + ".*";
        if (current.Equals(simpleWildcard, StringComparison.OrdinalIgnoreCase))
            return;

        // Find the ColumnList child node if present (granular parser), or fall back to
        // keeping everything up to and including the SELECT keyword token (legacy flat path).
        var columnListIdx2 = selectClause.Elements.FindIndex(
            e => e.Kind == SqlElementKind.Node && e.Node!.Kind == SqlSyntaxKind.ColumnList);

        if (columnListIdx2 >= 0)
        {
            selectClause.Elements.RemoveAt(columnListIdx2);

            // The ColumnList node's own leading trivia (newline + indent tokens) sits as sibling
            // elements immediately before the ColumnList slot. Those trivia tokens already supply
            // the newline that separates SELECT from the column expression, so the replacement
            // text must NOT start with an additional "\n". Strip all preceding whitespace/newline
            // trivia and emit the indent + expression directly, letting the retained newline
            // (the one after SELECT) do its job.
            var insertAt = columnListIdx2;
            while (insertAt > 0 && IsWhitespaceElement(selectClause.Elements[insertAt - 1]))
                selectClause.Elements.RemoveAt(--insertAt);

            selectClause.Elements.Insert(insertAt,
                SqlSyntaxElement.FromText("\n" + fromLeadingWs + "    " + simpleWildcard + "\n" + fromLeadingWs));
        }
        else
        {
            var kept = new List<SqlSyntaxElement>(2);
            foreach (var e in selectClause.Elements)
            {
                kept.Add(e);
                if (e.Kind == SqlElementKind.Token && e.Token.IsKeyword(SqlKeyword.Select))
                    break;
            }
            selectClause.Elements.Clear();
            selectClause.Elements.AddRange(kept);
            selectClause.Add("\n" + fromLeadingWs + "    " + simpleWildcard + "\n" + fromLeadingWs);
        }
    }

    private static void RemoveOptionalJoinLines(SqlNode fromClause, string sql)
    {
        // Used by the CTE path — no WHERE clause available, remove all optional joins unconditionally.
        RemoveUnreferencedJoinLines(fromClause, sql, whereClause: null);
    }

    /// <summary>
    /// Removes LEFT OUTER JOIN blocks (and any immediately preceding comment lines) from
    /// <paramref name="fromClause"/> when the join's alias is not referenced in
    /// <paramref name="whereClause"/>. When <paramref name="whereClause"/> is <c>null</c>,
    /// all LEFT OUTER JOIN blocks are removed unconditionally.
    /// A "block" spans from the LEFT keyword through to the end of the line containing the
    /// ON clause. That is, until the next newline after the paren depth returns to zero and ON
    /// has been seen, or until the next top-level JOIN/WHERE keyword.
    /// </summary>
    private static void RemoveUnreferencedJoinLines(SqlNode fromClause, string sql, SqlNode? whereClause = null)
    {
        // Segment the FROM clause elements into logical blocks:
        //   - Comment-only lines (LineComment/BlockComment + newline)
        //   - LEFT OUTER JOIN blocks (multi-line, paren-balanced)
        //   - Everything else
        // Then decide which blocks to drop.

        var segments = new List<(bool IsLeftOuterJoin, bool IsCommentLine, List<SqlSyntaxElement> Elements)>();
        var elements = fromClause.Elements;

        var i = 0;
        while (i < elements.Count)
        {
            // Check if this is a LEFT OUTER JOIN block start.
            // Skip leading whitespace to find the first significant token.
            var j = i;
            while (j < elements.Count && IsWhitespaceElement(elements[j])) j++;

            // Granular-tree path: a JoinClause RelationNode with JoinType.Left is the entire block.
            if (j < elements.Count && elements[j].Kind == SqlElementKind.Node &&
                elements[j].Node is SqlRelationNode { JoinType: JoinType.Left })
            {
                // Collect any leading whitespace trivia before the node plus the node itself.
                var block = new List<SqlSyntaxElement>();
                for (var k = i; k <= j; k++) block.Add(elements[k]);
                i = j + 1;
                segments.Add((true, false, block));
                continue;
            }

            if (j < elements.Count && elements[j].Kind == SqlElementKind.Token &&
                elements[j].Token.IsKeyword(SqlKeyword.Left))
            {
                // Consume the entire LEFT OUTER JOIN block:
                // everything up to and including the newline after the ON clause at paren depth 0.
                var block = new List<SqlSyntaxElement>();
                var depth = 0;
                var seenOn = false;
                while (i < elements.Count)
                {
                    var e = elements[i];
                    block.Add(e);
                    i++;
                    if (e.Kind == SqlElementKind.Token)
                    {
                        if (e.Token.Kind == SqlTokenKind.OpenParen) depth++;
                        else if (e.Token.Kind == SqlTokenKind.CloseParen) depth--;
                        else if (depth == 0 && e.Token.IsKeyword(SqlKeyword.On)) seenOn = true;
                        else if (depth == 0 && seenOn && e.Token.Kind == SqlTokenKind.NewLine)
                        {
                            // End of the ON clause line — but we may need to consume continuation lines
                            // (lines that don't start a new JOIN/WHERE/ORDER keyword at depth 0).
                            // Peek: if the next non-whitespace is AND/OR at depth 0, keep consuming.
                            while (i < elements.Count)
                            {
                                var k = i;
                                while (k < elements.Count && IsWhitespaceElement(elements[k])) k++;
                                if (k >= elements.Count) break;
                                var peek = elements[k];
                                if (peek.Kind == SqlElementKind.Token &&
                                    (peek.Token.IsKeyword(SqlKeyword.And) || peek.Token.IsKeyword(SqlKeyword.Or)))
                                {
                                    // Continuation of ON condition — consume until next newline.
                                    while (i < elements.Count)
                                    {
                                        var ce = elements[i];
                                        block.Add(ce);
                                        i++;
                                        if (ce is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.NewLine })
                                            break;
                                    }
                                }
                                else break;
                            }
                            break;
                        }
                    }
                }
                segments.Add((true, false, block));
                continue;
            }

            // Check if this is a comment-only line.
            if (j < elements.Count && elements[j].Kind == SqlElementKind.Token &&
                (elements[j].Token.Kind == SqlTokenKind.LineComment ||
                 elements[j].Token.Kind == SqlTokenKind.BlockComment))
            {
                var line = new List<SqlSyntaxElement>();
                while (i < elements.Count)
                {
                    var e = elements[i];
                    line.Add(e);
                    i++;
                    if (e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.NewLine })
                        break;
                }
                segments.Add((false, true, line));
                continue;
            }

            // Everything else: consume until next newline.
            var other = new List<SqlSyntaxElement>();
            while (i < elements.Count)
            {
                var e = elements[i];
                other.Add(e);
                i++;
                if (e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.NewLine })
                    break;
            }
            segments.Add((false, false, other));
        }

        // Collect all WHERE-and-after segments for alias reference checking.
        // Find the first segment that contains a top-level WHERE keyword.
        var whereAndAfterElements = whereClause is not null
            ? CollectWhereAndAfterElements(segments)
            : null;

        // Determine which segments to drop.
        var drop = new bool[segments.Count];
        for (var s = 0; s < segments.Count; s++)
        {
            var (isJoin, _, joinElements) = segments[s];
            if (!isJoin) continue;

            var alias = SqlTreeQueries.ExtractLeftOuterJoinAlias(joinElements, sql);
            // Build a temporary node from the WHERE-and-after elements for alias checking.
            var referenced = false;
            if (alias is not null && whereAndAfterElements is not null)
            {
                SqlToken? prev = null;
                foreach (var tok in SqlTreeQueries.EnumerateAllTokens(whereAndAfterElements))
                {
                    if (tok.Kind == SqlTokenKind.Dot && prev is { Kind: SqlTokenKind.Identifier } &&
                        SqlTreeQueries.IdentifierEquals(prev.Value.AsSpan(sql), alias))
                    { referenced = true; break; }
                    if (!tok.IsTrivia) prev = tok;
                }
            }

            if (referenced) continue;

            drop[s] = true;
            // Also, drop any immediately preceding comment-only lines.
            for (var p = s - 1; p >= 0; p--)
            {
                if (segments[p].IsCommentLine)
                    drop[p] = true;
                else
                    break;
            }
        }

        var result = new List<SqlSyntaxElement>(fromClause.Elements.Count);
        for (var s = 0; s < segments.Count; s++)
        {
            if (!drop[s])
                result.AddRange(segments[s].Elements);
        }

        fromClause.Elements.Clear();
        fromClause.Elements.AddRange(result);
    }

    private static List<SqlSyntaxElement>? CollectWhereAndAfterElements(
        List<(bool IsLeftOuterJoin, bool IsCommentLine, List<SqlSyntaxElement> Elements)> segments)
    {
        for (var s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            if (seg.IsLeftOuterJoin || seg.IsCommentLine) continue;
            var found = false;
            foreach (var tok in SqlTreeQueries.EnumerateAllTokens(seg.Elements))
            {
                if (tok is not { Kind: SqlTokenKind.Keyword, Keyword: SqlKeyword.Where }) continue;
                found = true;
                break;
            }
            if (!found) continue;
            var result = new List<SqlSyntaxElement>();
            for (var ws = s; ws < segments.Count; ws++)
                result.AddRange(segments[ws].Elements);
            return result;
        }
        return null;
    }

    private static bool IsWhitespaceElement(SqlSyntaxElement e) =>
        e is { Kind: SqlElementKind.Token, Token.Kind: SqlTokenKind.Whitespace or SqlTokenKind.NewLine };

    private static string? GetTableAliasInFromClause(SqlNode fromClause, string tableName, string sql)
    {
        // Granular-tree path: look for a TableRelation SqlRelationNode whose first significant
        // identifier token matches tableName, and return the alias by reading the last identifier
        // token in the relation node from the source SQL.
        foreach (var e in fromClause.Elements)
        {
            if (e.Kind == SqlElementKind.Node && e.Node is SqlRelationNode { Kind: SqlSyntaxKind.TableRelation } rel)
            {
                SqlToken? tableNameToken = null;
                SqlToken? aliasToken = null;
                foreach (var t in EnumerateSignificantTokens(rel))
                {
                    if (t.Kind == SqlTokenKind.Identifier)
                    {
                        if (tableNameToken is null) tableNameToken = t;
                        else aliasToken = t; // last identifier is the alias
                    }
                }
                if (tableNameToken is not null &&
                    SqlTreeQueries.IdentifierEquals(tableNameToken.Value.AsSpan(sql), tableName))
                {
                    if (aliasToken is not null)
                        return aliasToken.Value.ToString(sql);
                    // Fall back to parsing the position-string stored in rel.Alias.
                    if (rel.Alias is { } aliasPos)
                    {
                        var colon = aliasPos.IndexOf(':');
                        if (colon > 0 &&
                            int.TryParse(aliasPos.Substring(0, colon), out var start) &&
                            int.TryParse(aliasPos.Substring(colon + 1), out var len))
                            return sql.Substring(start, len);
                    }
                    return null;
                }
            }
        }

        // Flat-token fallback path.
        var state = 0;
        foreach (var t in EnumerateSignificantTokens(fromClause))
        {
            switch (state)
            {
                case 0:
                    if (t.IsKeyword(SqlKeyword.From)) state = 1;
                    break;
                case 1:
                    if (t.Kind == SqlTokenKind.Identifier &&
                        SqlTreeQueries.IdentifierEquals(t.AsSpan(sql), tableName))
                        state = 2;
                    else return null;
                    break;
                case 2:
                    if (t.IsKeyword(SqlKeyword.As)) { state = 3; break; }
                    if (t.Kind == SqlTokenKind.Identifier)
                        return t.ToString(sql);
                    return null;
                case 3:
                    if (t.Kind == SqlTokenKind.Identifier) return t.ToString(sql);
                    return null;
            }
        }
        return null;
    }

    private static IEnumerable<SqlToken> EnumerateSignificantTokens(SqlNode node)
    {
        foreach (var e in node.Elements)
        {
            if (e.Kind == SqlElementKind.Token)
            {
                if (!e.Token.IsTrivia) yield return e.Token;
            }
            else if (e.Kind == SqlElementKind.Node)
            {
                foreach (var t in EnumerateSignificantTokens(e.Node!)) yield return t;
            }
        }
    }

    private static string GetSelectColumnsText(SqlNode selectClause, string sql)
    {
        var sb = new StringBuilder();
        var chars = sql.ToCharArray();
        var afterSelect = false;
        using var sw = new StringWriter(sb);
        foreach (var e in selectClause.Elements)
        {
            if (!afterSelect)
            {
                if (e.Kind == SqlElementKind.Token && e.Token.IsKeyword(SqlKeyword.Select))
                    afterSelect = true;
                continue;
            }
            // If the parser produced a ColumnList child node, write its contents directly
            // so the output is identical to the flat-token representation.
            if (e.Kind == SqlElementKind.Node && e.Node!.Kind == SqlSyntaxKind.ColumnList)
                e.Node.Write(sw, chars);
            else
                e.Write(sw, chars);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the line indent (whitespace after the last newline) immediately before
    /// <paramref name="childNode"/> within <paramref name="parent"/>'s element list.
    /// </summary>
    private static string GetNodeLineIndent(SqlNode parent, SqlNode childNode)
    {
        var idx = parent.Elements.FindIndex(e => e.Kind == SqlElementKind.Node && ReferenceEquals(e.Node, childNode));
        if (idx <= 0) return string.Empty;
        var stack = new Stack<char>();
        WalkBackForWhitespace(parent, idx - 1, stack);
        var sb = new StringBuilder();
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static string GetLeadingWhitespaceOfFirstSignificantToken(SqlNode node, string sql)
    {
        var sb = new StringBuilder();
        foreach (var e in node.Elements)
        {
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                {
                    var span = e.Token.AsSpan(sql);
                    for (var i = 0; i < span.Length; i++) sb.Append(span[i]);
                }
                else if (e.Token.Kind == SqlTokenKind.NewLine)
                {
                    sb.Clear();
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }
        return sb.ToString();
    }

    private static void TrimTrailingTrivia(SqlNode node)
    {
        while (node.Elements.Count > 0)
        {
            var last = node.Elements[^1];
            if (last is { Kind: SqlElementKind.Token, Token.IsTrivia: true })
                node.Elements.RemoveAt(node.Elements.Count - 1);
            else if (last.Kind == SqlElementKind.Node)
            {
                TrimTrailingTrivia(last.Node!);
                break;
            }
            else break;
        }
    }
}
