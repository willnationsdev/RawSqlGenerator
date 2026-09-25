// SqlFormatter.cs

using System.Text;
using SpiritSync.Generators.Parser.SqlNodes;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ForCanBeConvertedToForeach

namespace SpiritSync.Generators.Parser;

/// <summary>
/// A formatting wrapper around a <see cref="SqlParser"/>'s parse tree that applies
/// purely presentational changes to SQL text (indentation and spacing normalization).
/// </summary>
/// <remarks>
/// <para>
/// Obtained via <see cref="SqlParser.Format(System.Action{SqlFormatter})"/>. Like
/// <see cref="SqlTransformer"/>, a <see cref="SqlFormatter"/> holds a private clone of
/// the parser's cached parse tree so mutations never affect the originating
/// <see cref="SqlParser"/>. Call <see cref="Build"/> when all formatting options have
/// been configured; it serializes the mutated tree and returns a fresh
/// <see cref="SqlParser"/> whose <see cref="SqlParser.Source"/> reflects the formatted text.
/// </para>
/// <para>
/// Formatting options are stored in a <see cref="SqlFormatOptions"/> instance exposed via
/// <see cref="Options"/>. Unlike <see cref="SqlTransformer"/>, formatters do not add or
/// remove semantic content — they only adjust whitespace and layout.
/// </para>
/// </remarks>
internal sealed class SqlFormatter : SqlVisitor, ISqlFormatter
{
    // -----------------------------------------------------------------------
    // ISqlFormatter
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public SqlFormatOptions Options { get; }

    // -----------------------------------------------------------------------
    // Constructor & tree access
    // -----------------------------------------------------------------------

    internal SqlFormatter(SqlFormatOptions options)
    {
        Options = options;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public const string OrderByPrefix = "ORDER BY ";

    public override SqlVisitFlow Visit(SqlNode node)
    {
        if (node.Parent is not null) return SqlVisitFlow.Break;

        var root = node;

        if (Options.SplitOrderByColumns)
            ApplySplitOrderByColumns(root);

        if (Options.NormalizeSubqueryIndentation)
            ApplySubqueryIndentationNormalization(root, Options.IndentSize, Options.InlineSubqueryLengthThreshold);

        if (Options.AlignWhereAndOrContinuations)
            ApplyWhereAndOrAlignment(root);

        return SqlVisitFlow.Continue;
    }

    // -----------------------------------------------------------------------
    // Formatting implementations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Splits each comma-separated column expression in every top-level and CTE
    /// <c>ORDER BY</c> clause onto its own line. Continuation lines are indented to
    /// align with the first column expression (i.e., by the width of <c>"ORDER BY "</c>).
    /// </summary>
    private void ApplySplitOrderByColumns(SqlNode root)
    {
        new OrderByColumnSplitter(this).Visit(root);
    }

    /// <summary>
    /// A <see cref="SqlVisitor"/> that locates every <see cref="SqlSyntaxKind.OrderByClause"/>
    /// node in the tree and delegates to <see cref="SqlFormatter.SplitOrderByColumnsInClause"/>,
    /// tracking the nearest enclosing <see cref="SqlSyntaxKind.Query"/> as the
    /// owning statement context.
    /// </summary>
    private sealed class OrderByColumnSplitter(SqlFormatter formatter) : SqlVisitor
    {
        private SqlNode? _currentStatement;

        public override SqlVisitFlow PreVisitQuery(SqlNode node)
        {
            _currentStatement = node;
            return SqlVisitFlow.Continue;
        }

        public override SqlVisitFlow PostVisitQuery(SqlNode node)
        {
            _currentStatement = null;
            return SqlVisitFlow.Continue;
        }

        public override SqlVisitFlow PreVisitOrderByClause(SqlNode node)
        {
            formatter.SplitOrderByColumnsInClause(node, _currentStatement);
            return SqlVisitFlow.Continue;
        }
    }

    /// <summary>
    /// Rewrites <paramref name="orderByNode"/> so that each comma-separated column expression
    /// appears on its own line, with continuation lines indented to align with the first column.
    /// The transformation works directly on the AST structure without serializing to text.
    /// </summary>
    private void SplitOrderByColumnsInClause(SqlNode orderByNode, SqlNode? owningStatement)
    {
        var root = orderByNode.GetRoot();

        // -----------------------------------------------------------------------
        // Step 1: Locate the BY keyword inside the ORDER BY node so we know where
        //         the column-expression portion begins.
        // -----------------------------------------------------------------------
        var byTokenIndex = -1;
        for (var i = 0; i < orderByNode.Elements.Count; i++)
        {
            var e = orderByNode.Elements[i];
            if (e.Kind == SqlElementKind.Token && e.Token.IsKeyword(SqlKeyword.By))
            {
                byTokenIndex = i;
                break;
            }
        }

        if (byTokenIndex < 0) return;

        // -----------------------------------------------------------------------
        // Step 2: Check whether the columns are already split (a newline token or
        //         a text element containing '\n' follows the BY token). If so, skip.
        // -----------------------------------------------------------------------
        for (var i = byTokenIndex + 1; i < orderByNode.Elements.Count; i++)
        {
            var e = orderByNode.Elements[i];
            if (e.Kind == SqlElementKind.Token && e.Token.Kind == SqlTokenKind.NewLine)
                return;
            if (e.Kind == SqlElementKind.Text && e.Text!.Contains('\n'))
                return;
        }

        // -----------------------------------------------------------------------
        // Step 3: Collect the post-BY elements into a SqlColumnList node. Each
        //         element is carried over as-is so no text serialization is needed.
        //         We also detect whether there are multiple top-level comma-separated
        //         expressions by scanning for Comma tokens at paren depth 0.
        // -----------------------------------------------------------------------

        // Collect all elements that follow the BY keyword.
        var postByElements = new List<SqlSyntaxElement>(orderByNode.Elements.Count - byTokenIndex - 1);
        for (var i = byTokenIndex + 1; i < orderByNode.Elements.Count; i++)
            postByElements.Add(orderByNode.Elements[i]);

        // Walk postByElements to find top-level comma positions (paren depth 0).
        var commaIndices = new List<int>();
        var depth = 0;
        for (var i = 0; i < postByElements.Count; i++)
        {
            var e = postByElements[i];
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.OpenParen)       depth++;
                else if (e.Token.Kind == SqlTokenKind.CloseParen) depth--;
                else if (e.Token.Kind == SqlTokenKind.Comma && depth == 0)
                    commaIndices.Add(i);
            }
        }

        // Nothing to split if there are no top-level commas (single-column ORDER BY).
        if (commaIndices.Count == 0) return;

        // -----------------------------------------------------------------------
        // Step 4: Build a SqlColumnList node whose elements are the post-BY content.
        //         The ColumnList node groups all column expressions and their
        //         separators; it is then embedded in the rebuilt OrderByClause.
        // -----------------------------------------------------------------------
        var columnListNode = new SqlNode(SqlSyntaxKind.ColumnList);
        foreach (var e in postByElements)
            columnListNode.Elements.Add(e);

        // -----------------------------------------------------------------------
        // Step 5: Determine the leading whitespace before the ORDER keyword so
        //         continuation lines can be aligned to the first column expression.
        // -----------------------------------------------------------------------
        var orderLeadingWs = GetLeadingWhitespaceBeforeOrderBy(orderByNode, owningStatement);

        // Continuation lines are indented by the length of the ORDER BY prefix
        // relative to its own indentation.
        var continuationIndent = orderLeadingWs + new string(' ', OrderByPrefix.Length);

        // -----------------------------------------------------------------------
        // Step 6: Split the ColumnList elements into per-column groups at each
        //         top-level comma, then rebuild the ColumnList with explicit
        //         newline-and-indent text elements between columns.
        // -----------------------------------------------------------------------

        // Build a list of element-groups: one group per column expression.
        // commaIndices gives us the positions of the commas inside postByElements.
        var groups = new List<List<SqlSyntaxElement>>();
        var groupStart = 0;
        foreach (var ci in commaIndices)
        {
            // Slice from groupStart up to (but not including) the comma.
            var group = new List<SqlSyntaxElement>();
            for (var k = groupStart; k < ci; k++)
                group.Add(postByElements[k]);
            groups.Add(group);
            groupStart = ci + 1; // skip the comma itself
        }
        // Last group: everything after the final comma.
        var lastGroup = new List<SqlSyntaxElement>();
        for (var k = groupStart; k < postByElements.Count; k++)
            lastGroup.Add(postByElements[k]);
        groups.Add(lastGroup);

        // Detect trailing whitespace in the last group (after the last column expression)
        // so that anything following the ORDER BY clause (e.g. closing paren of CTE) stays
        // correctly aligned. Trailing elements are whitespace or newline tokens / text.
        var trailingElements = new List<SqlSyntaxElement>();
        if (lastGroup.Count > 0)
        {
            var trimIdx = lastGroup.Count;
            while (trimIdx > 0)
            {
                var e = lastGroup[trimIdx - 1];
                var isWs = (e.Kind == SqlElementKind.Token &&
                            (e.Token.Kind == SqlTokenKind.Whitespace || e.Token.Kind == SqlTokenKind.NewLine))
                           || (e.Kind == SqlElementKind.Text && string.IsNullOrWhiteSpace(e.Text));
                if (!isWs) break;
                trimIdx--;
            }
            for (var k = trimIdx; k < lastGroup.Count; k++)
                trailingElements.Add(lastGroup[k]);
            // Remove the trailing elements from the last group so they are emitted separately.
            while (lastGroup.Count > trimIdx)
                lastGroup.RemoveAt(lastGroup.Count - 1);
        }

        // Rebuild the ColumnList: first column inline (after a leading space), subsequent
        // columns each preceded by a comma, a newline, and the continuation indent.
        columnListNode.Elements.Clear();

        // First column: emit a leading space then the column's elements (trimmed of leading ws).
        columnListNode.Elements.Add(SqlSyntaxElement.FromText(" "));
        var firstGroup = TrimLeadingWhitespaceElements(groups[0]);
        foreach (var e in firstGroup)
            columnListNode.Elements.Add(e);

        // Remaining columns.
        for (var gi = 1; gi < groups.Count; gi++)
        {
            // Comma + newline + indent.
            columnListNode.Elements.Add(SqlSyntaxElement.FromText(",\n" + continuationIndent));
            var colGroup = TrimLeadingWhitespaceElements(groups[gi]);
            foreach (var e in colGroup)
                columnListNode.Elements.Add(e);
        }

        // Restore trailing whitespace (e.g. newline + indent before a closing paren).
        if (trailingElements.Count > 0)
        {
            // Emit trailing elements as a single text node so layout is preserved.
            columnListNode.Elements.Add(SqlSyntaxElement.FromText("\n" + orderLeadingWs));
        }

        // -----------------------------------------------------------------------
        // Step 7: Rebuild the ORDER BY node, keeping the preamble (ORDER, WS, BY)
        //         and appending the rebuilt ColumnList child node.
        // -----------------------------------------------------------------------
        var replacement = new SqlNode(SqlSyntaxKind.OrderByClause);

        // Copy the preamble: everything up to and including the BY token.
        for (var i = 0; i <= byTokenIndex; i++)
            replacement.Elements.Add(orderByNode.Elements[i]);

        // Append the ColumnList node as a structured child.
        replacement.Add(columnListNode);

        // -----------------------------------------------------------------------
        // Step 8: Splice the rebuilt node back into the tree.
        // -----------------------------------------------------------------------
        if (owningStatement is not null)
        {
            var idx = owningStatement.Elements.FindIndex(
                e => e.Kind == SqlElementKind.Node && ReferenceEquals(e.Node, orderByNode));
            if (idx >= 0)
                owningStatement.Elements[idx] = SqlSyntaxElement.FromNode(replacement);
        }
        else if (root is not null)
        {
            // No owning statement tracked: walk the tree from root to find and replace.
            ReplaceNodeInTree(root, orderByNode, replacement);
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="elements"/> with leading whitespace/newline
    /// elements removed, so columns can be placed immediately after a comma-separator.
    /// </summary>
    private static List<SqlSyntaxElement> TrimLeadingWhitespaceElements(List<SqlSyntaxElement> elements)
    {
        var start = 0;
        while (start < elements.Count)
        {
            var e = elements[start];
            var isWs = (e.Kind == SqlElementKind.Token &&
                        (e.Token.Kind == SqlTokenKind.Whitespace || e.Token.Kind == SqlTokenKind.NewLine))
                       || (e.Kind == SqlElementKind.Text && string.IsNullOrWhiteSpace(e.Text));
            if (!isWs) break;
            start++;
        }
        return start == 0 ? elements : elements.GetRange(start, elements.Count - start);
    }

    /// <summary>
    /// Returns the line-leading whitespace (characters after the last newline before the
    /// ORDER keyword) in the context of the owning statement, or an empty string if not found.
    /// </summary>
    private string GetLeadingWhitespaceBeforeOrderBy(SqlNode orderByNode, SqlNode? owningStatement)
    {
        if (owningStatement is null) return string.Empty;

        var idx = owningStatement.Elements.FindIndex(
            e => e.Kind == SqlElementKind.Node && ReferenceEquals(e.Node, orderByNode));
        if (idx <= 0) return string.Empty;

        var stack = new Stack<char>();
        for (var i = idx - 1; i >= 0; i--)
        {
            var e = owningStatement.Elements[i];
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine) break;
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                {
                    for (var k = 0; k < e.Token.Length; k++) stack.Push(' ');
                }
                else break;
            }
            else if (e.Kind == SqlElementKind.Node)
            {
                // Walk back through child node looking for trailing whitespace.
                if (WalkBackNodeForIndent(e.Node!, stack)) break;
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                var text = e.Text!;
                var lastNl = text.LastIndexOf('\n');
                if (lastNl >= 0)
                {
                    var after = text.Substring(lastNl + 1);
                    foreach (var ch in after)
                        if (ch == ' ' || ch == '\t') stack.Push(ch);
                    break;
                }
                if (text.Trim().Length == 0)
                    foreach (var ch in text) stack.Push(ch);
                else break;
            }
        }

        var sb = new StringBuilder();
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static bool WalkBackNodeForIndent(SqlNode node, Stack<char> stack)
    {
        for (var i = node.Elements.Count - 1; i >= 0; i--)
        {
            var e = node.Elements[i];
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine) return true;
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                    for (var k = 0; k < e.Token.Length; k++) stack.Push(' ');
                else return true;
            }
            else if (e.Kind == SqlElementKind.Node)
            {
                if (WalkBackNodeForIndent(e.Node!, stack)) return true;
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

    /// <summary>
    /// Recursively searches <paramref name="root"/> for <paramref name="target"/> and
    /// replaces it with <paramref name="replacement"/>.
    /// </summary>
    private static bool ReplaceNodeInTree(SqlNode root, SqlNode target, SqlNode replacement)
    {
        for (var i = 0; i < root.Elements.Count; i++)
        {
            var e = root.Elements[i];
            if (e.Kind != SqlElementKind.Node) continue;
            if (ReferenceEquals(e.Node, target))
            {
                root.Elements[i] = SqlSyntaxElement.FromNode(replacement);
                return true;
            }
            if (ReplaceNodeInTree(e.Node!, target, replacement)) return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // AST-level subquery indentation normalization
    // -----------------------------------------------------------------------

    /// <summary>
    /// Visits every <see cref="SqlSyntaxKind.FromClause"/> and
    /// <see cref="SqlSyntaxKind.SelectClause"/> node in <paramref name="root"/> and
    /// rewrites any long <c>(SELECT …)</c> subquery spans so the opening <c>(</c> ends its
    /// line, the body is indented by <paramref name="indentSize"/> spaces, and — when
    /// appropriate — the closing <c>)</c> gets its own line.  The entire transformation
    /// operates on the element list directly; no text serialization is performed.
    /// </summary>
    private static void ApplySubqueryIndentationNormalization(SqlNode root, int indentSize, int inlineThreshold)
    {
        new SubqueryIndentationNormalizer(indentSize, inlineThreshold).Visit(root);
    }

    /// <summary>
    /// A <see cref="SqlVisitor"/> that normalizes subquery indentation in every
    /// <see cref="SqlSyntaxKind.FromClause"/> and <see cref="SqlSyntaxKind.SelectClause"/> node
    /// by walking the element list directly without text serialization.
    /// </summary>
    private sealed class SubqueryIndentationNormalizer(int indentSize, int inlineThreshold) : SqlVisitor
    {
        public override SqlVisitFlow PostVisitFromClause(SqlNode node)
        {
            NormalizeNode(node);
            return SqlVisitFlow.Continue;
        }

        public override SqlVisitFlow PostVisitSelectClause(SqlNode node)
        {
            NormalizeNode(node);
            return SqlVisitFlow.Continue;
        }

        public override SqlVisitFlow PostVisitColumnExpression(SqlNode node)
        {
            NormalizeNode(node);
            return SqlVisitFlow.Continue;
        }

        public override SqlVisitFlow PostVisitPredicate(SqlNode node)
        {
            // Normalize the predicate's own element list. For EXISTS predicates the
            // OpenParen that wraps the subquery lives here — before the SelectClause
            // child node — so this is where NormalizeSubqueriesInElements must look.
            NormalizeNode(node);
            return SqlVisitFlow.Continue;
        }

        private void NormalizeNode(SqlNode node)
        {
            NormalizeSubqueriesInElements(node.Elements, indentSize, inlineThreshold);
        }
    }

    /// <summary>
    /// Walks <paramref name="elements"/> looking for <c>OpenParen + [trivia] + SELECT</c>
    /// sequences (flat tokens) or <c>OpenParen + Query node + CloseParen</c> sequences
    /// (produced by the parser for subqueries inside column expressions) at paren-depth 0.
    /// For each such subquery that exceeds <paramref name="inlineThreshold"/> characters when
    /// collapsed, the element run is replaced so that:
    /// <list type="bullet">
    ///   <item><description>The <c>(</c> stays on its current line.</description></item>
    ///   <item><description>The subquery body is indented by <paramref name="indentSize"/> extra spaces.</description></item>
    ///   <item><description>When <c>(</c> was the first non-whitespace character on its line, the
    ///     closing <c>)</c> is placed on its own line at the original indentation.</description></item>
    /// </list>
    /// </summary>
    private static void NormalizeSubqueriesInElements(List<SqlSyntaxElement> elements, int indentSize, int inlineThreshold)
    {
        var i = 0;
        while (i < elements.Count)
        {
            var e = elements[i];

            // Only look for OpenParen tokens at the flat element level.
            if (e.Kind != SqlElementKind.Token || e.Token.Kind != SqlTokenKind.OpenParen)
            {
                i++;
                continue;
            }

            // Case 1: OpenParen immediately followed (skipping trivia) by a Query child node,
            // then a CloseParen token — produced by the parser for (SELECT ...) inside column
            // expressions.
            var nextIdx = i + 1;
            while (nextIdx < elements.Count && elements[nextIdx].Kind == SqlElementKind.Token &&
                   (elements[nextIdx].Token.Kind == SqlTokenKind.Whitespace ||
                    elements[nextIdx].Token.Kind == SqlTokenKind.NewLine))
                nextIdx++;

            if (nextIdx < elements.Count &&
                elements[nextIdx].Kind == SqlElementKind.Node &&
                elements[nextIdx].Node!.Kind == SqlSyntaxKind.Query)
            {
                var queryNodeIdx = nextIdx;
                var closeIdx2 = queryNodeIdx + 1;
                // Skip any trivia between the Query node and the closing ')'.
                while (closeIdx2 < elements.Count && elements[closeIdx2].Kind == SqlElementKind.Token &&
                       (elements[closeIdx2].Token.Kind == SqlTokenKind.Whitespace ||
                        elements[closeIdx2].Token.Kind == SqlTokenKind.NewLine))
                    closeIdx2++;

                if (closeIdx2 < elements.Count &&
                    elements[closeIdx2].Kind == SqlElementKind.Token &&
                    elements[closeIdx2].Token.Kind == SqlTokenKind.CloseParen)
                {
                    // Expand if the subquery is already multi-line OR its collapsed length exceeds
                    // the inline threshold. A multi-line subquery should never be kept inline
                    // regardless of character count.
                    var queryNode2 = elements[queryNodeIdx].Node!;
                    var isMultiLine2 = queryNode2.ContainsNewLine();
                    var collapsedLen2 = 1 + elements[queryNodeIdx].EstimateLength() + 1;
                    if (isMultiLine2 || collapsedLen2 > inlineThreshold)
                    {
                        var lineIndent2 = GetLineIndentBeforeIndex(elements, i);
                        // Accumulate IndentScope from the query node up through its ancestors.
                        var totalScope2 = 0;
                        for (var anc = queryNode2; anc is not null; anc = anc.Parent)
                            totalScope2 += anc.IndentScope;
                        if (totalScope2 <= 0) totalScope2 = 1;
                        var bodyIndent2 = lineIndent2 + new string(' ', totalScope2 * indentSize);
                        var parenIsFirst2 = IsFirstSignificantOnLine(elements, i);

                        // Mutate trivia in place: replace the whitespace/newline tokens between
                        // '(' and the Query node with a single newline+indent Text element,
                        // and replace the trivia between the Query node and ')' with the
                        // appropriate newline+indent (or nothing) Text element.
                        // Remove trivia tokens between '(' (at i) and queryNodeIdx.
                        var triviaBeforeCount = queryNodeIdx - i - 1;
                        if (triviaBeforeCount > 0)
                            elements.RemoveRange(i + 1, triviaBeforeCount);
                        // queryNodeIdx has shifted; recalculate.
                        elements.Insert(i + 1, SqlSyntaxElement.FromText("\n" + bodyIndent2));
                        var newQueryIdx = i + 2; // after the inserted text

                        // Remove trivia tokens between Query node and ')'.
                        // closeIdx2 was relative to original positions; recalculate.
                        var newCloseSearch = newQueryIdx + 1;
                        while (newCloseSearch < elements.Count &&
                               elements[newCloseSearch].Kind == SqlElementKind.Token &&
                               (elements[newCloseSearch].Token.Kind == SqlTokenKind.Whitespace ||
                                elements[newCloseSearch].Token.Kind == SqlTokenKind.NewLine))
                            elements.RemoveAt(newCloseSearch);
                        // newCloseSearch now points at ')' (or wherever it landed).
                        if (parenIsFirst2)
                        {
                            // Replace the ')' token with a Text element that includes the newline+indent.
                            elements[newCloseSearch] = SqlSyntaxElement.FromText("\n" + lineIndent2 + ")");
                        }
                        // else: ')' stays as-is (no trivia needed before it).

                        i = newCloseSearch + 1;
                        continue;
                    }
                }
            }

            // Case 2: OpenParen + [trivia] + SELECT token (flat token sequence).
            // Peek ahead (skipping whitespace) to see if the next meaningful token is SELECT.
            var selectIdx = i + 1;
            while (selectIdx < elements.Count)
            {
                var pe = elements[selectIdx];
                if (pe.Kind == SqlElementKind.Token &&
                    (pe.Token.Kind == SqlTokenKind.Whitespace || pe.Token.Kind == SqlTokenKind.NewLine))
                { selectIdx++; continue; }
                break;
            }

            if (selectIdx >= elements.Count ||
                elements[selectIdx].Kind != SqlElementKind.Token ||
                !elements[selectIdx].Token.IsKeyword(SqlKeyword.Select))
            {
                i++;
                continue;
            }

            // Found '(' [trivia] SELECT — locate the matching ')' and measure collapsed length.
            var closeIdx = FindMatchingCloseParenIndex(elements, i);
            if (closeIdx < 0) { i++; continue; }

            // Collapsed length = 1 ('(') + sum of non-newline token lengths in body + 1 (')').
            var collapsedLen = 1; // '('
            for (var k = i + 1; k < closeIdx; k++)
            {
                var be = elements[k];
                if (be.Kind == SqlElementKind.Token)
                    collapsedLen += be.Token.Kind == SqlTokenKind.NewLine ? 1 : be.Token.Length;
                else if (be.Kind == SqlElementKind.Text)
                    collapsedLen += be.Text!.Length;
            }
            collapsedLen += 1; // ')'

            if (collapsedLen <= inlineThreshold) { i++; continue; }

            // Determine the leading indent of the line on which '(' sits.
            var lineIndent = GetLineIndentBeforeIndex(elements, i);
            var bodyIndent = lineIndent + new string(' ', indentSize);

            // Was '(' the first non-whitespace character on its line?
            var parenIsFirstSignificant = IsFirstSignificantOnLine(elements, i);

            // Mutate trivia in place for Case 2 (flat token sequence).
            // Step A: Remove leading trivia between '(' and the first body token (SELECT),
            //         then insert a single newline+bodyIndent Text element.
            var firstBodyIdx = i + 1;
            while (firstBodyIdx < closeIdx &&
                   elements[firstBodyIdx].Kind == SqlElementKind.Token &&
                   (elements[firstBodyIdx].Token.Kind == SqlTokenKind.Whitespace ||
                    elements[firstBodyIdx].Token.Kind == SqlTokenKind.NewLine))
            {
                elements.RemoveAt(firstBodyIdx);
                closeIdx--;
            }
            elements.Insert(firstBodyIdx, SqlSyntaxElement.FromText("\n" + bodyIndent));
            closeIdx++;

            // Step B: Re-indent each newline within the body so continuation lines use bodyIndent.
            // Determine the minimum existing indentation of body lines (after the first).
            var minBodyLineIndent = int.MaxValue;
            var scanInLine = false;
            var scanWsCount = 0;
            for (var k = firstBodyIdx + 1; k < closeIdx; k++)
            {
                var be = elements[k];
                if (be.Kind == SqlElementKind.Token && be.Token.Kind == SqlTokenKind.NewLine)
                {
                    scanInLine = true;
                    scanWsCount = 0;
                }
                else if (scanInLine)
                {
                    if (be.Kind == SqlElementKind.Token && be.Token.Kind == SqlTokenKind.Whitespace)
                        scanWsCount += be.Token.Length;
                    else
                    {
                        if (scanWsCount < minBodyLineIndent) minBodyLineIndent = scanWsCount;
                        scanInLine = false;
                    }
                }
            }
            if (minBodyLineIndent == int.MaxValue) minBodyLineIndent = 0;

            // Walk the body replacing each NewLine token + following whitespace tokens with
            // a single Text element containing newline+bodyIndent (stripping minBodyLineIndent).
            var bk = firstBodyIdx + 1;
            while (bk < closeIdx)
            {
                var be = elements[bk];
                if (be.Kind == SqlElementKind.Token && be.Token.Kind == SqlTokenKind.NewLine)
                {
                    // Replace this NewLine token with newline+bodyIndent text.
                    elements[bk] = SqlSyntaxElement.FromText("\n" + bodyIndent);
                    bk++;
                    // Remove following whitespace tokens up to minBodyLineIndent chars.
                    var wsRemaining = minBodyLineIndent;
                    while (bk < closeIdx && wsRemaining > 0 &&
                           elements[bk].Kind == SqlElementKind.Token &&
                           elements[bk].Token.Kind == SqlTokenKind.Whitespace)
                    {
                        var wsLen = elements[bk].Token.Length;
                        if (wsLen <= wsRemaining)
                        {
                            elements.RemoveAt(bk);
                            closeIdx--;
                            wsRemaining -= wsLen;
                        }
                        else
                        {
                            // Partial: replace with remainder whitespace text.
                            elements[bk] = SqlSyntaxElement.FromText(new string(' ', wsLen - wsRemaining));
                            wsRemaining = 0;
                            bk++;
                        }
                    }
                    continue;
                }
                bk++;
            }

            // Step C: Handle the closing ')'.
            if (parenIsFirstSignificant)
            {
                // Replace the ')' token with a Text element that includes newline+lineIndent.
                elements[closeIdx] = SqlSyntaxElement.FromText("\n" + lineIndent + ")");
            }
            // else: ')' token stays as-is.

            i = closeIdx + 1;
        }
    }

    /// <summary>
    /// Returns the index of the <c>)</c> element that closes the <c>(</c> at
    /// <paramref name="openIdx"/> in <paramref name="elements"/>, or -1 if not found.
    /// Only Token-level elements are counted for paren depth.
    /// </summary>
    private static int FindMatchingCloseParenIndex(List<SqlSyntaxElement> elements, int openIdx)
    {
        var depth = 1; // we already consumed the opening '('
        for (var i = openIdx + 1; i < elements.Count; i++)
        {
            var e = elements[i];
            if (e.Kind != SqlElementKind.Token) continue;
            if (e.Token.Kind == SqlTokenKind.OpenParen) depth++;
            else if (e.Token.Kind == SqlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the leading whitespace string of the line on which the element at
    /// <paramref name="idx"/> sits by scanning backwards through
    /// <paramref name="elements"/> to find the preceding newline.
    /// </summary>
    private static string GetLineIndentBeforeIndex(List<SqlSyntaxElement> elements, int idx)
    {
        // Walk backwards from idx-1 to find the last newline, then collect whitespace after it.
        var stack = new Stack<char>();
        for (var i = idx - 1; i >= 0; i--)
        {
            var e = elements[i];
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine) break;
                if (e.Token.Kind == SqlTokenKind.Whitespace)
                {
                    for (var k = 0; k < e.Token.Length; k++) stack.Push(' ');
                    continue;
                }
            }
            else if (e.Kind == SqlElementKind.Node)
            {
                // Walk back through child node looking for trailing whitespace / newline.
                if (WalkBackNodeForIndent(e.Node!, stack)) break;
                continue;
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                var text = e.Text!;
                var nl = text.LastIndexOf('\n');
                if (nl >= 0)
                {
                    // Count whitespace after the last newline in this text element.
                    for (var k = nl + 1; k < text.Length; k++)
                        if (text[k] == ' ' || text[k] == '\t') stack.Push(text[k]); else break;
                    break;
                }
                // All whitespace text before a newline — add its length.
                if (string.IsNullOrWhiteSpace(text))
                {
                    foreach (var ch in text) stack.Push(ch);
                    continue;
                }
            }
            // Hit a non-whitespace, non-newline element — stop.
            stack.Clear();
            break;
        }
        var sb = new StringBuilder();
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    /// <summary>
    /// Returns <c>true</c> when the element at <paramref name="idx"/> is the first
    /// non-whitespace element on its line (i.e. all elements between the preceding
    /// newline and <paramref name="idx"/> are whitespace).
    /// </summary>
    private static bool IsFirstSignificantOnLine(List<SqlSyntaxElement> elements, int idx)
    {
        for (var i = idx - 1; i >= 0; i--)
        {
            var e = elements[i];
            if (e.Kind == SqlElementKind.Token)
            {
                if (e.Token.Kind == SqlTokenKind.NewLine) return true;
                if (e.Token.Kind == SqlTokenKind.Whitespace) continue;
                return false;
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                var text = e.Text!;
                var nl = text.LastIndexOf('\n');
                if (nl >= 0)
                {
                    // Check all characters after the last newline.
                    for (var k = nl + 1; k < text.Length; k++)
                        if (text[k] != ' ' && text[k] != '\t') return false;
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(text)) return false;
            }
            else return false; // Node element — treat as non-whitespace
        }
        return true; // start of element list
    }

    // -----------------------------------------------------------------------
    // AST-level WHERE AND/OR alignment
    // -----------------------------------------------------------------------

    /// <summary>
    /// Visits every <see cref="SqlSyntaxKind.FromClause"/> node in <paramref name="root"/>
    /// and re-indents top-level <c>AND</c>/<c>OR</c> continuation keywords so their right
    /// edges align with the right edge of <c>WHERE</c>.  The transformation walks the
    /// element list directly; no text serialization is performed.
    /// </summary>
    private static void ApplyWhereAndOrAlignment(SqlNode root)
    {
        new WhereAndOrAligner().Visit(root);
    }

    /// <summary>
    /// A <see cref="SqlVisitor"/> that aligns AND/OR continuations in every
    /// <see cref="SqlSyntaxKind.FromClause"/> node (WHERE lives inside FromClause in the AST)
    /// by walking the element list directly without text serialization.
    /// </summary>
    private sealed class WhereAndOrAligner : SqlVisitor
    {
        public override SqlVisitFlow PostVisitFromClause(SqlNode node)
        {
            AlignAndOrInElements(node.Elements);
            return SqlVisitFlow.Continue;
        }
    }

    /// <summary>
    /// Walks <paramref name="elements"/> and re-indents any <c>AND</c>/<c>OR</c> keyword
    /// tokens that immediately follow a newline and are at paren-depth 0 inside a WHERE
    /// clause, so that:
    /// <list type="bullet">
    ///   <item><description><c>AND</c> (3 chars) → indent = whereIndent + 2</description></item>
    ///   <item><description><c>OR</c>  (2 chars) → indent = whereIndent + 3</description></item>
    /// </list>
    /// The preceding <see cref="SqlTokenKind.Whitespace"/> token (or inserted
    /// <see cref="SqlElementKind.Text"/> element) is replaced to achieve the target indent.
    /// </summary>
    private static void AlignAndOrInElements(List<SqlSyntaxElement> elements)
    {
        // whereIndent = -1 means we are not currently inside a WHERE clause.
        var whereIndent = -1;
        // Paren depth relative to the WHERE line (0 = same level as WHERE).
        var parenDepth = 0;
        // Are we at the logical start of a new line (i.e. right after a newline)?
        var atLineStart = false;
        // How many whitespace chars have been accumulated since the last newline?
        var lineWsCount = 0;

        var i = 0;
        while (i < elements.Count)
        {
            var e = elements[i];

            if (e.Kind == SqlElementKind.Token)
            {
                var tok = e.Token;

                if (tok.Kind == SqlTokenKind.NewLine)
                {
                    atLineStart = true;
                    lineWsCount = 0;
                    i++;
                    continue;
                }

                if (tok.Kind == SqlTokenKind.Whitespace)
                {
                    if (atLineStart) lineWsCount += tok.Length;
                    i++;
                    continue;
                }

                // Non-whitespace token: compute indent for this line.
                var tokenLineIndent = atLineStart ? lineWsCount : -1;
                atLineStart = false;

                if (tok.Kind == SqlTokenKind.Keyword)
                {
                    if (tok.IsKeyword(SqlKeyword.Where))
                    {
                        // Enter WHERE scope: record its indentation.
                        whereIndent = tokenLineIndent >= 0 ? tokenLineIndent : 0;
                        parenDepth = 0;
                        i++;
                        continue;
                    }

                    if (whereIndent >= 0)
                    {
                        // A clause-level keyword at <= whereIndent ends the WHERE scope.
                        if (tokenLineIndent >= 0 && tokenLineIndent <= whereIndent &&
                            IsClauseKeyword(tok))
                        {
                            whereIndent = -1;
                            i++;
                            continue;
                        }

                        // AND / OR at paren-depth 0: re-indent.
                        if (parenDepth == 0 && tokenLineIndent >= 0 &&
                            (tok.IsKeyword(SqlKeyword.And) || tok.IsKeyword(SqlKeyword.Or)))
                        {
                            var targetIndent = whereIndent + (tok.IsKeyword(SqlKeyword.And) ? 2 : 3);
                            // Replace or insert the preceding whitespace to match targetIndent.
                            SetIndentBeforeIndex(elements, i, targetIndent);
                            // `i` may have shifted if we inserted an element; re-read the AND/OR.
                            // Either way, advance past it.
                            i++;
                            continue;
                        }
                    }
                }

                // Track paren depth.
                if (whereIndent >= 0)
                {
                    if (tok.Kind == SqlTokenKind.OpenParen) parenDepth++;
                    else if (tok.Kind == SqlTokenKind.CloseParen) parenDepth--;
                }
            }
            else if (e.Kind == SqlElementKind.Text)
            {
                // Text elements (from prior transformations): scan for newlines to maintain
                // line-start tracking, but do not adjust their content.
                var text = e.Text!;
                var nl = text.LastIndexOf('\n');
                if (nl >= 0)
                {
                    atLineStart = true;
                    lineWsCount = 0;
                    for (var k = nl + 1; k < text.Length; k++)
                        if (text[k] == ' ' || text[k] == '\t') lineWsCount++; else break;
                }
                if (whereIndent >= 0)
                {
                    foreach (var c in text)
                    {
                        if (c == '(') parenDepth++;
                        else if (c == ')') parenDepth--;
                    }
                }
            }

            i++;
        }
    }

    /// <summary>
    /// Sets the whitespace immediately before element <paramref name="idx"/> in
    /// <paramref name="elements"/> to exactly <paramref name="targetIndent"/> spaces.
    /// If the element at <c>idx-1</c> is a <see cref="SqlTokenKind.Whitespace"/> token,
    /// it is replaced with a <see cref="SqlElementKind.Text"/> element of the right width.
    /// If no whitespace element precedes, one is inserted.
    /// </summary>
    private static void SetIndentBeforeIndex(List<SqlSyntaxElement> elements, int idx, int targetIndent)
    {
        var indentText = new string(' ', targetIndent);
        if (idx > 0)
        {
            var prev = elements[idx - 1];
            if (prev.Kind == SqlElementKind.Token && prev.Token.Kind == SqlTokenKind.Whitespace)
            {
                elements[idx - 1] = SqlSyntaxElement.FromText(indentText);
                return;
            }
            if (prev.Kind == SqlElementKind.Text && string.IsNullOrWhiteSpace(prev.Text) && !prev.Text!.Contains('\n'))
            {
                elements[idx - 1] = SqlSyntaxElement.FromText(indentText);
                return;
            }
        }
        // No whitespace predecessor — insert one.
        elements.Insert(idx, SqlSyntaxElement.FromText(indentText));
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="token"/> is a SQL clause keyword that signals
    /// the end of a WHERE clause (FROM, SELECT, ORDER, GROUP, HAVING, UNION, INTERSECT,
    /// EXCEPT, FETCH, OFFSET, WHERE, JOIN keywords, ELSE, END, THEN, or closing paren).
    /// </summary>
    private static bool IsClauseKeyword(SqlToken token)
    {
        if (token.Kind == SqlTokenKind.CloseParen) return true;
        if (token.Kind != SqlTokenKind.Keyword) return false;
        return token.Keyword is
            SqlKeyword.From or SqlKeyword.Select or SqlKeyword.Order or
            SqlKeyword.Group or SqlKeyword.Having or SqlKeyword.Union or
            SqlKeyword.Intersect or SqlKeyword.Except or SqlKeyword.Fetch or
            SqlKeyword.Offset or SqlKeyword.Where or
            SqlKeyword.Inner or SqlKeyword.Left or SqlKeyword.Right or
            SqlKeyword.Full or SqlKeyword.Outer or SqlKeyword.Cross or
            SqlKeyword.Join or SqlKeyword.On or
            SqlKeyword.Else or SqlKeyword.End or SqlKeyword.Then;
    }
}
