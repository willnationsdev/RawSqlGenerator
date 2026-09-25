// SqlParser.cs

using SpiritSync.Generators.Parser.SqlNodes;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// A reusable, instance-based SQL parser that owns a source string and its lossless
/// <see cref="SqlNode"/> parse tree.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="SqlParser"/> is the single "entry point" to the parsing API. Given a SQL
/// string, it tokenizes and structures it exactly once (in a single left-to-right pass)
/// and caches the result. All subsequent queries and transformations reuse that cached
/// tree, so callers pay the parse cost only when the underlying text changes.
/// </para>
/// <para>
/// Operations that only inspect the tree are exposed via <see cref="Query"/> as a
/// <see cref="SqlParserQuery"/> wrapper. Operations that would change the SQL text
/// (transformations, indentation normalization) are exposed via <see cref="Transform"/>
/// or <see cref="PrepareForRawString"/> and always produce a <em>new</em>
/// <see cref="SqlParser"/> instance so the original is left untouched.
/// </para>
/// <para>
/// The parser is intentionally minimal — it only structures the constructs that the
/// <c>RawSqlGenerator</c> needs to transform (WITH/CTE, SELECT, FROM, ORDER BY);
/// everything else is captured verbatim so <see cref="SqlNode.ToString(string)"/> can
/// reproduce the source byte-for-byte.
/// </para>
/// </remarks>
internal sealed class SqlParser
{
    /// <summary>Creates a parser over <paramref name="source"/>. The text is parsed lazily on first access.</summary>
    public SqlParser(string? source) : this(source, null) { }

    /// <summary>Creates a parser over <paramref name="source"/> with an associated <paramref name="formatter"/>.</summary>
    public SqlParser(string? source, ISqlFormatter? formatter) : this(source, formatter, null) { }

    /// <summary>Creates a parser over <paramref name="source"/> with an associated <paramref name="formatter"/> and an optional <paramref name="sqlConstantName"/>.</summary>
    public SqlParser(string? source, ISqlFormatter? formatter, string? sqlConstantName)
    {
        Source = source ?? "";
        Formatter = formatter;
        SqlConstantName = sqlConstantName;
    }

    /// <summary>The original source string this parser was created with.</summary>
    public string Source { get; }

    /// <summary>The formatter associated with this parser instance, if any.</summary>
    public ISqlFormatter? Formatter { get; }

    /// <summary>The name of the generated SQL constant for which <see cref="Source"/> was computed, or <c>null</c> if the source was not read from a file.</summary>
    public string? SqlConstantName { get; }

    /// <summary>The lossless parse tree for <see cref="Source"/>. Built once and cached.</summary>
    public SqlNode Root => field ??= BuildTree(Source, SqlConstantName);

    /// <summary>Shared character buffer for token slice writes. Built once per parser lifetime.</summary>
    internal char[] SourceChars => field ??= Source.ToCharArray();

    /// <summary>Returns an inspection wrapper exposing read-only queries over the parse tree.</summary>
    public SqlParserQuery Query() => new(this);

    /// <summary>Returns a transformation wrapper. All mutating methods operate on a private clone of the tree.</summary>
    public SqlTransformer Transform() => new(this);

    public void Format()
    {
        Formatter?.Visit(Root);
    }

    /// <summary>
    /// Returns <see cref="Source"/> re-indented for embedding as a raw string constant.
    /// Only whitespace/indentation is changed, so no reparse is needed.
    /// </summary>
    public string PrepareForRawString() => SqlText.PrepareSqlForRawString(Source);

    /// <summary>Renders the parse tree back to text. If the tree is unmodified, this equals <see cref="Source"/>.</summary>
    public override string ToString() => Root.ToString(Source);

    /// <summary>
    /// Tokenizes <paramref name="source"/> into an array of <see cref="SqlToken"/>s.
    /// Exposed as a standalone helper for callers that only need the token stream.
    /// </summary>
    private static SqlToken[] Tokenize(string source)
    {
        var list = new List<SqlToken>(Math.Max(16, source.Length / 4));
        var tokenizer = new SqlTokenizer(source.AsSpan());
        while (tokenizer.MoveNext())
            list.Add(tokenizer.Current);
        return list.ToArray();
    }

    // ---------------------------------------------------------------------
    // Tree construction (single pass over the token stream)
    // ---------------------------------------------------------------------

    private static SqlNode BuildTree(string source, string? fileName)
    {
        var tokens = Tokenize(source);
        var pos = 0;
        var script = new SqlNode(SqlSyntaxKind.Script) { SqlConstantName = fileName };

        ConsumeTrivia(tokens, ref pos, script);

        if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.With))
        {
            script.Add(ParseWithClause(tokens, ref pos, source));
            ConsumeTrivia(tokens, ref pos, script);
        }

        if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.Select))
        {
            script.Add(ParseSelectStatement(tokens, ref pos, source, /*insideCteBody*/ false));
        }

        AppendRemainingAsRaw(tokens, ref pos, script);
        return script;
    }

    // ---------------------------------------------------------------------
    // Grammar productions
    // ---------------------------------------------------------------------

    private static SqlNode ParseWithClause(SqlToken[] tokens, ref int pos, string source)
    {
        var with = new SqlNode(SqlSyntaxKind.WithClause);
        with.Add(tokens[pos++]); // WITH

        while (pos < tokens.Length)
        {
            ConsumeTrivia(tokens, ref pos, with);

            if (pos >= tokens.Length || tokens[pos].Kind != SqlTokenKind.Identifier)
                break;

            var cte = new SqlNode(SqlSyntaxKind.CommonTableExpression, with);
            ParseCteInto(tokens, ref pos, source, cte);

            var savedPos = pos;
            ConsumeTrivia(tokens, ref pos, with);
            if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.Comma)
            {
                with.Add(tokens[pos++]);
                continue;
            }

            _ = savedPos;
            break;
        }
        return with;
    }

    private static void ParseCteInto(SqlToken[] tokens, ref int pos, string source, SqlNode cte)
    {
        cte.Add(tokens[pos++]); // alias identifier
        ConsumeTrivia(tokens, ref pos, cte);

        if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.OpenParen)
            AppendBalancedParenGroupAsRaw(tokens, ref pos, cte);

        ConsumeTrivia(tokens, ref pos, cte);

        if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.As))
        {
            cte.Add(tokens[pos++]);
            ConsumeTrivia(tokens, ref pos, cte);
        }

        if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.OpenParen)
        {
            cte.Add(tokens[pos++]); // '('
            ParseCteBodyInto(tokens, ref pos, source, new SqlNode(SqlSyntaxKind.CteBody, cte));
            if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.CloseParen)
                cte.Add(tokens[pos++]); // ')'
        }
    }

    private static void ParseCteBodyInto(SqlToken[] tokens, ref int pos, string source, SqlNode body)
    {
        ConsumeTrivia(tokens, ref pos, body);

        if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.Select))
            ParseSelectStatementInto(tokens, ref pos, source, insideCteBody: true, new SqlNode(SqlSyntaxKind.Query, body));

        while (pos < tokens.Length && tokens[pos].Kind != SqlTokenKind.CloseParen)
            body.Add(tokens[pos++]);
    }

    private static SqlNode ParseSelectStatement(SqlToken[] tokens, ref int pos, string source, bool insideCteBody)
    {
        var stmt = new SqlNode(SqlSyntaxKind.Query);
        ParseSelectStatementInto(tokens, ref pos, source, insideCteBody, stmt);
        return stmt;
    }

    private static void ParseSelectStatementInto(SqlToken[] tokens, ref int pos, string source, bool insideCteBody, SqlNode stmt)
    {
        var select = new SqlSelectNode(SqlSyntaxKind.SelectClause, stmt);
        select.Add(tokens[pos++]); // SELECT

        // Collect optional DISTINCT / ALL / TOP modifiers before the column list
        ConsumeTrivia(tokens, ref pos, select);
        while (pos < tokens.Length &&
               (tokens[pos].IsKeyword(SqlKeyword.Distinct) ||
                tokens[pos].IsKeyword(SqlKeyword.All) ||
                tokens[pos].IsKeyword(SqlKeyword.Top)))
        {
            select.Add(tokens[pos++]);
            if (tokens[pos - 1].IsKeyword(SqlKeyword.Top))
            {
                // TOP (n) or TOP n — consume the argument
                ConsumeTrivia(tokens, ref pos, select);
                if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.OpenParen)
                    AppendBalancedParenGroupAsRaw(tokens, ref pos, select);
                else if (pos < tokens.Length)
                    select.Add(tokens[pos++]);
            }
            ConsumeTrivia(tokens, ref pos, select);
        }

        ParseColumnListInto(tokens, ref pos, source, select, insideCteBody, stopKeyword: SqlKeyword.From);

        if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.From))
        {
            var from = new SqlNode(SqlSyntaxKind.FromClause, stmt);
            from.Add(tokens[pos++]); // FROM
            CopyUntilTopLevel(tokens, ref pos, from, initialParenDepth: 0, stopKeyword: SqlKeyword.Order, stopAtCloseParen: insideCteBody);
        }

        if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.Order))
        {
            var order = new SqlNode(SqlSyntaxKind.OrderByClause, stmt);
            order.Add(tokens[pos++]); // ORDER
            var save = pos;
            ConsumeTrivia(tokens, ref pos, order);
            if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.By))
                order.Add(tokens[pos++]);
            else
                pos = save;

            ParseColumnListInto(tokens, ref pos, source, order, insideCteBody, stopKeyword: SqlKeyword.None);
        }
    }

    // ---------------------------------------------------------------------
    // Column-list parsing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parses a comma-separated column/expression list into a <see cref="SqlColumnListNode"/>
    /// appended to <paramref name="parent"/>. Each column becomes a <see cref="SqlExpressionNode"/>
    /// with its <see cref="SqlExpressionNode.Alias"/> populated when an <c>AS alias</c> (or bare
    /// alias) is present.
    /// </summary>
    private static void ParseColumnListInto(SqlToken[] tokens, ref int pos, string source, SqlNode parent, bool insideCteBody, SqlKeyword stopKeyword)
    {
        var list = new SqlColumnListNode(SqlSyntaxKind.ColumnList, parent);

        while (pos < tokens.Length)
        {
            // Leading trivia before each column expression
            ConsumeTrivia(tokens, ref pos, list);

            if (pos >= tokens.Length) break;
            var t = tokens[pos];

            // Stop conditions
            if (t.Kind == SqlTokenKind.CloseParen && insideCteBody) break;
            if (stopKeyword != SqlKeyword.None && t.IsKeyword(stopKeyword)) break;

            // Comma separator between columns — attach to the list, not a column node
            if (t.Kind == SqlTokenKind.Comma)
            {
                list.Add(tokens[pos++]);
                continue;
            }

            // Parse one column expression
            var col = new SqlExpressionNode(SqlSyntaxKind.ColumnExpression, list);
            ParseColumnExpressionInto(tokens, ref pos, source, col, insideCteBody, stopKeyword, out var alias);
            col.Alias = alias;
            list.Columns.Add(col);
        }
    }

    /// <summary>
    /// Reads tokens for a single column expression (up to the next top-level comma or stop
    /// keyword) into <paramref name="col"/> and extracts the trailing alias if present.
    /// </summary>
    private static void ParseColumnExpressionInto(
        SqlToken[] tokens, ref int pos,
        string source,
        SqlExpressionNode col,
        bool insideCteBody,
        SqlKeyword stopKeyword,
        out string? alias)
    {
        alias = null;
        var depth = 0;

        // We collect all tokens for the expression first, then look back for the alias.
        // Alias forms:  expr AS name  |  expr name  (bare alias, identifier only)
        // We track the last two meaningful (non-trivia) token positions so we can
        // identify "... AS <ident>" or "... <ident>" patterns.
        var lastIdentPos = -1;   // index of last identifier token added
        var secondLastPos = -1;  // index of token before that
        var lastIsAs = false;

        while (pos < tokens.Length)
        {
            var t = tokens[pos];

            if (t.Kind == SqlTokenKind.OpenParen)
            {
                // Peek ahead (skipping trivia) to detect (SELECT ...) subqueries so they are
                // parsed as proper Query sub-nodes rather than flat raw tokens.
                var peekIdx = pos + 1;
                while (peekIdx < tokens.Length && tokens[peekIdx].IsTrivia) peekIdx++;
                if (peekIdx < tokens.Length && tokens[peekIdx].IsKeyword(SqlKeyword.Select))
                {
                    // Emit the opening '(' as a raw token, parse the inner SELECT as a Query
                    // node (insideCteBody:true so it stops at the matching ')'), then emit ')'.
                    col.Add(t); pos++; // '('
                    var subquery = new SqlNode(SqlSyntaxKind.Query, col) { IndentScope = 1 };
                    ParseSelectStatementInto(tokens, ref pos, source, insideCteBody: true, subquery);
                    col.Add(subquery);
                    if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.CloseParen)
                    {
                        col.Add(tokens[pos++]); // ')'
                    }
                    // Update alias-tracking: the ')' is the last meaningful element.
                    secondLastPos = lastIdentPos;
                    lastIsAs = false;
                    lastIdentPos = col.Elements.Count - 1;
                    continue;
                }
                depth++;
                col.Add(t);
                pos++;
                continue;
            }
            if (t.Kind == SqlTokenKind.CloseParen)
            {
                if (depth == 0 && insideCteBody) break;
                if (depth == 0) break; // shouldn't happen at column level but guard anyway
                depth--;
                col.Add(t);
                pos++;
                continue;
            }

            // Top-level comma or stop keyword ends this column
            if (depth == 0 && t.Kind == SqlTokenKind.Comma) break;
            if (depth == 0 && stopKeyword != SqlKeyword.None && t.IsKeyword(stopKeyword)) break;

            // Top-level CASE keyword: parse into a structured SqlCaseExpressionNode.
            if (depth == 0 && t.IsKeyword(SqlKeyword.Case))
            {
                // Construct without parent so we can add it after parsing (avoids double-add).
                var caseNode = new SqlCaseExpressionNode();
                ParseCaseExpressionInto(tokens, ref pos, source, caseNode);
                col.Add(caseNode); // sets caseNode.Parent = col and appends to col.Elements
                secondLastPos = lastIdentPos;
                lastIsAs = false;
                lastIdentPos = col.Elements.Count - 1;
                continue;
            }

            col.Add(t);

            if (!t.IsTrivia)
            {
                secondLastPos = lastIdentPos;
                lastIsAs = t.IsKeyword(SqlKeyword.As);
                lastIdentPos = col.Elements.Count - 1;
            }

            pos++;
        }

        // Extract alias: last identifier preceded by AS keyword, or bare trailing identifier
        // (but not a wildcard '*' or a keyword that is not an alias).
        if (lastIdentPos >= 0)
        {
            var lastElem = col.Elements[lastIdentPos];
            if (lastElem.Kind == SqlElementKind.Token &&
                lastElem.Token.Kind == SqlTokenKind.Identifier)
            {
                if (lastIsAs || secondLastPos < 0 ||
                    (col.Elements[secondLastPos].Kind == SqlElementKind.Token &&
                     col.Elements[secondLastPos].Token.IsKeyword(SqlKeyword.As)))
                {
                    // The alias is the last identifier token's text, resolved directly from source.
                    var tok = lastElem.Token;
                    alias = source.Substring(tok.Start, tok.Length);
                }
                else
                {
                    // Bare alias: last token is an identifier not preceded by AS.
                    // Only treat it as an alias if the second-to-last is not also an identifier
                    // (which would indicate a qualified name like schema.table).
                    var prev = col.Elements[secondLastPos];
                    if (prev.Kind == SqlElementKind.Token && prev.Token.Kind != SqlTokenKind.Dot)
                    {
                        var tok = lastElem.Token;
                        alias = source.Substring(tok.Start, tok.Length);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Token-stream helpers
    // ---------------------------------------------------------------------

    private static void CopyUntilTopLevel(
        SqlToken[] tokens,
        ref int pos,
        SqlNode target,
        int initialParenDepth,
        SqlKeyword stopKeyword,
        bool stopAtCloseParen)
    {
        var depth = initialParenDepth;
        while (pos < tokens.Length)
        {
            var t = tokens[pos];
            if (t.Kind == SqlTokenKind.OpenParen) { depth++; target.Add(t); pos++; continue; }
            if (t.Kind == SqlTokenKind.CloseParen)
            {
                if (depth == 0 && stopAtCloseParen) return;
                depth--;
                target.Add(t);
                pos++;
                continue;
            }
            if (depth == 0 && stopKeyword != SqlKeyword.None && t.IsKeyword(stopKeyword))
                return;

            target.Add(t);
            pos++;
        }
    }

    private static void AppendBalancedParenGroupAsRaw(SqlToken[] tokens, ref int pos, SqlNode target)
    {
        var depth = 0;
        while (pos < tokens.Length)
        {
            var t = tokens[pos];
            target.Add(t);
            pos++;
            if (t.Kind == SqlTokenKind.OpenParen) depth++;
            else if (t.Kind == SqlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0) return;
            }
        }
    }

    private static void ConsumeTrivia(SqlToken[] tokens, ref int pos, SqlNode target)
    {
        while (pos < tokens.Length && tokens[pos].IsTrivia)
            target.Add(tokens[pos++]);
    }

    private static void AppendRemainingAsRaw(SqlToken[] tokens, ref int pos, SqlNode target)
    {
        if (pos >= tokens.Length) return;
        var raw = new SqlNode(SqlSyntaxKind.RawTokens, target);
        while (pos < tokens.Length)
            raw.Add(tokens[pos++]);
    }

    // ---------------------------------------------------------------------
    // CASE expression parser
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parses a <c>CASE WHEN … THEN … [ELSE …] END</c> expression into
    /// <paramref name="caseNode"/>, starting at the <c>CASE</c> keyword token.
    /// On return <paramref name="pos"/> points past the <c>END</c> token.
    /// </summary>
    private static void ParseCaseExpressionInto(
        SqlToken[] tokens, ref int pos, string source, SqlCaseExpressionNode caseNode)
    {
        // Consume the CASE keyword itself.
        caseNode.Add(tokens[pos++]); // CASE

        while (pos < tokens.Length)
        {
            // Skip trivia between clauses.
            while (pos < tokens.Length && tokens[pos].IsTrivia)
                caseNode.Add(tokens[pos++]);

            if (pos >= tokens.Length) break;
            var t = tokens[pos];

            if (t.IsKeyword(SqlKeyword.When))
            {
                var whenNode = new SqlCaseWhenExpressionNode();
                whenNode.Add(tokens[pos++]); // WHEN

                // Parse the predicate (everything up to THEN).
                var predicateNode = new SqlPredicateNode();
                ParsePredicateInto(tokens, ref pos, source, predicateNode, stopAtKeyword: SqlKeyword.Then);
                whenNode.Predicate = predicateNode;
                whenNode.Add(predicateNode); // sets predicateNode.Parent = whenNode

                // Consume THEN + result tokens (up to next WHEN / ELSE / END).
                if (pos < tokens.Length && tokens[pos].IsKeyword(SqlKeyword.Then))
                    whenNode.Add(tokens[pos++]); // THEN

                // Result expression: consume until WHEN / ELSE / END at depth 0.
                var depth = 0;
                while (pos < tokens.Length)
                {
                    var r = tokens[pos];
                    if (r.Kind == SqlTokenKind.OpenParen) { depth++; whenNode.Add(r); pos++; continue; }
                    if (r.Kind == SqlTokenKind.CloseParen)
                    {
                        if (depth == 0) break;
                        depth--;
                        whenNode.Add(r);
                        pos++;
                        continue;
                    }
                    if (depth == 0 && (r.IsKeyword(SqlKeyword.When) ||
                                       r.IsKeyword(SqlKeyword.Else) ||
                                       r.IsKeyword(SqlKeyword.End))) break;
                    whenNode.Add(r);
                    pos++;
                }
                caseNode.Add(whenNode); // sets whenNode.Parent = caseNode
                caseNode.WhenClauses.Add(whenNode);
                continue;
            }

            if (t.IsKeyword(SqlKeyword.Else))
            {
                var elseNode = new SqlCaseElseExpressionNode();
                elseNode.Add(tokens[pos++]); // ELSE

                // Result expression: consume until END at depth 0.
                var depth = 0;
                while (pos < tokens.Length)
                {
                    var r = tokens[pos];
                    if (r.Kind == SqlTokenKind.OpenParen) { depth++; elseNode.Add(r); pos++; continue; }
                    if (r.Kind == SqlTokenKind.CloseParen)
                    {
                        if (depth == 0) break;
                        depth--;
                        elseNode.Add(r);
                        pos++;
                        continue;
                    }
                    if (depth == 0 && r.IsKeyword(SqlKeyword.End)) break;
                    elseNode.Add(r);
                    pos++;
                }
                caseNode.Add(elseNode); // sets elseNode.Parent = caseNode
                caseNode.ElseClause = elseNode;
                continue;
            }

            if (t.IsKeyword(SqlKeyword.End))
            {
                caseNode.Add(tokens[pos++]); // END
                break;
            }

            // Anything else (e.g. a bare expression after CASE before first WHEN) — consume flat.
            caseNode.Add(tokens[pos++]);
        }
    }

    // ---------------------------------------------------------------------
    // Predicate parser
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parses a predicate expression into <paramref name="predicateNode"/> until
    /// <paramref name="stopAtKeyword"/> is encountered at depth 0 (the stop token is
    /// <em>not</em> consumed).
    /// <para>
    /// Top-level <c>AND</c>/<c>OR</c> operators split the predicate into child
    /// <see cref="SqlPredicateNode"/> instances stored in
    /// <see cref="SqlPredicateNode.Children"/>. Each leaf child holds the tokens for one
    /// atomic predicate; any <c>EXISTS(SELECT …)</c> within a leaf is parsed as a
    /// <see cref="SqlSyntaxKind.Query"/> child node exposed via
    /// <see cref="SqlPredicateNode.ExistsQuery"/>. The parent composite node's flat
    /// <see cref="SqlNode.Elements"/> list still contains all tokens (including the
    /// AND/OR tokens) so that <c>Write()</c> produces correct SQL output.
    /// </para>
    /// </summary>
    private static void ParsePredicateInto(
        SqlToken[] tokens, ref int pos, string source,
        SqlPredicateNode predicateNode, SqlKeyword stopAtKeyword)
    {
        // ── Pass 1: collect every token / sub-query node into allItems,
        //            recording the index of each top-level AND/OR token.
        // allItems elements are either SqlToken or SqlNode (EXISTS sub-query).
        var allItems = new List<object>();
        var splitIndices = new List<int>(); // indices in allItems of AND/OR tokens
        var depth = 0;

        while (pos < tokens.Length)
        {
            var t = tokens[pos];

            if (t.Kind == SqlTokenKind.OpenParen)
            {
                // Detect EXISTS(SELECT …): last non-trivia item must be the EXISTS keyword.
                if (depth == 0 && LastNonTriviaItemAsToken(allItems).IsKeyword(SqlKeyword.Exists))
                {
                    var peekIdx = pos + 1;
                    while (peekIdx < tokens.Length && tokens[peekIdx].IsTrivia) peekIdx++;
                    if (peekIdx < tokens.Length && tokens[peekIdx].IsKeyword(SqlKeyword.Select))
                    {
                        allItems.Add(t); pos++; // '('
                        var subquery = new SqlNode(SqlSyntaxKind.Query) { IndentScope = 1 };
                        ParseSelectStatementInto(tokens, ref pos, source, insideCteBody: true, subquery);
                        allItems.Add(subquery);
                        if (pos < tokens.Length && tokens[pos].Kind == SqlTokenKind.CloseParen)
                        {
                            allItems.Add(tokens[pos]); pos++;
                        }
                        continue;
                    }
                }

                depth++;
                allItems.Add(t);
                pos++;
                continue;
            }

            if (t.Kind == SqlTokenKind.CloseParen)
            {
                if (depth == 0) break;
                depth--;
                allItems.Add(t);
                pos++;
                continue;
            }

            if (depth == 0 && stopAtKeyword != SqlKeyword.None && t.IsKeyword(stopAtKeyword)) break;

            if (depth == 0 && (t.IsKeyword(SqlKeyword.And) || t.IsKeyword(SqlKeyword.Or)))
                splitIndices.Add(allItems.Count);

            allItems.Add(t);
            pos++;
        }

        // ── Pass 2: build the node tree from allItems + splitIndices.
        if (splitIndices.Count == 0)
        {
            // Leaf predicate — no top-level AND/OR.
            PopulateLeafPredicate(predicateNode, allItems, source);
            return;
        }

        // Composite predicate: operator is determined by the first split token.
        var firstOpToken = (SqlToken)allItems[splitIndices[0]];
        predicateNode.Operator = firstOpToken.IsKeyword(SqlKeyword.And) ? "AND" : "OR";

        // Build child ranges between (and around) the split-point tokens.
        // Range `i` covers allItems[rangeStart[i] .. rangeEnd[i]).
        var rangeStarts = new List<int> { 0 };
        var rangeEnds   = new List<int>();
        foreach (var si in splitIndices)
        {
            rangeEnds.Add(si);      // child ends before the AND/OR token
            rangeStarts.Add(si + 1); // next child starts after it
        }
        rangeEnds.Add(allItems.Count);

        var fullText = new System.Text.StringBuilder();

        for (var ci = 0; ci < rangeStarts.Count; ci++)
        {
            var start = rangeStarts[ci];
            var end   = rangeEnds[ci];

            // Emit the AND/OR token (and surrounding trivia) into the parent's flat list
            // so that Write() still produces correct SQL.
            if (ci > 0)
            {
                var opIdx = splitIndices[ci - 1];
                // trivia between previous child end and the operator
                for (var ti = rangeEnds[ci - 1]; ti < opIdx; ti++)
                    if (allItems[ti] is SqlToken triviaTok)
                        predicateNode.Elements.Add(SqlSyntaxElement.FromToken(triviaTok));
                // the operator token itself
                if (allItems[opIdx] is SqlToken opTok)
                    predicateNode.Elements.Add(SqlSyntaxElement.FromToken(opTok));
                // trivia between operator and next child start
                for (var ti = opIdx + 1; ti < rangeStarts[ci]; ti++)
                    if (allItems[ti] is SqlToken triviaTok2)
                        predicateNode.Elements.Add(SqlSyntaxElement.FromToken(triviaTok2));

                fullText.Append(' ').Append(predicateNode.Operator).Append(' ');
            }

            // Trim leading/trailing trivia from the child's item range.
            while (start < end && allItems[start] is SqlToken lt && lt.IsTrivia) start++;
            while (end > start && allItems[end - 1] is SqlToken tt && tt.IsTrivia) end--;

            var childItems = allItems.GetRange(start, end - start);
            var childPredicateNode  = new SqlPredicateNode();
            PopulateLeafPredicate(childPredicateNode, childItems, source);

            predicateNode.Add(childPredicateNode);   // sets childPredicateNode.Parent, appends to predicateNode.Elements
            predicateNode.Children.Add(childPredicateNode);
            fullText.Append(childPredicateNode.PredicateText);
        }

        predicateNode.PredicateText = fullText.ToString().Trim();
    }

    /// <summary>
    /// Populates a leaf <see cref="SqlPredicateNode"/> from a pre-collected list of items
    /// (each item is either a <see cref="SqlToken"/> or a <see cref="SqlNode"/> for an
    /// EXISTS sub-query). Sets <see cref="SqlPredicateNode.PredicateText"/> and, when an
    /// EXISTS sub-query node is present, <see cref="SqlPredicateNode.ExistsQuery"/>.
    /// </summary>
    private static void PopulateLeafPredicate(SqlPredicateNode predicateNode, List<object> items, string source)
    {
        var textBuilder = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            if (item is SqlToken tok)
            {
                predicateNode.Elements.Add(SqlSyntaxElement.FromToken(tok));
                if (!tok.IsTrivia)
                    textBuilder.Append(source, tok.Start, tok.Length);
                else if (tok.Kind == SqlTokenKind.Whitespace || tok.Kind == SqlTokenKind.NewLine)
                    textBuilder.Append(' ');
            }
            else if (item is SqlNode node)
            {
                predicateNode.Add(node); // sets node.Parent = predicateNode
                if (node.Kind == SqlSyntaxKind.Query && predicateNode.ExistsQuery is null)
                    predicateNode.ExistsQuery = node;
                textBuilder.Append("EXISTS(...)");
            }
        }
        predicateNode.PredicateText = textBuilder.ToString().Trim();
    }

    /// <summary>
    /// Returns the last non-trivia <see cref="SqlToken"/> in a mixed
    /// <c>List&lt;object&gt;</c> (items are <see cref="SqlToken"/> or <see cref="SqlNode"/>),
    /// or <c>default</c> if none.
    /// </summary>
    private static SqlToken LastNonTriviaItemAsToken(List<object> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is SqlToken tok && !tok.IsTrivia) return tok;
        }
        return default;
    }
}
