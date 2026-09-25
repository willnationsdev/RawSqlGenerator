// SqlVisitor.cs

using SpiritSync.Generators.Parser.SqlNodes;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Controls whether a tree walk should continue to sibling and child nodes or stop immediately.
/// </summary>
internal enum SqlVisitFlow
{
    /// <summary>Continue walking the tree normally.</summary>
    Continue,

    /// <summary>Stop the tree walk immediately and return to the caller.</summary>
    Break
}

interface ISqlVisitor
{
    SqlVisitFlow Visit(SqlNode node);
}

/// <summary>
/// Base class for visitors over the <see cref="SqlNode"/> parse tree produced by
/// <see cref="SqlParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// Override the <c>PreVisit*</c> and <c>PostVisit*</c> methods you care about. The default
/// implementation of every method returns <see cref="SqlVisitFlow.Continue"/> so you only
/// need to override the nodes relevant to your transformation or analysis.
/// </para>
/// <para>
/// Drive the walk by calling <see cref="Visit"/> with the root node. The walker performs a
/// depth-first, pre-order + post-order traversal: <c>PreVisit*</c> is called before
/// descending into children; <c>PostVisit*</c> is called after all children have been visited.
/// Returning <see cref="SqlVisitFlow.Break"/> from any method halts the entire walk.
/// </para>
/// </remarks>
internal abstract class SqlVisitor : ISqlVisitor
{
    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Walks <paramref name="node"/> depth-first, dispatching pre- and post-visit calls for
    /// each node encountered. Returns <see cref="SqlVisitFlow.Break"/> if the walk was
    /// stopped early, otherwise <see cref="SqlVisitFlow.Continue"/>.
    /// </summary>
    public virtual SqlVisitFlow Visit(SqlNode node)
    {
        // Pre-visit dispatch
        var flow = node.Kind switch
        {
            SqlSyntaxKind.Script               => PreVisitScript(node),
            SqlSyntaxKind.WithClause           => PreVisitWithClause(node),
            SqlSyntaxKind.CommonTableExpression => PreVisitCommonTableExpression(node),
            SqlSyntaxKind.CteBody              => PreVisitCteBody(node),
            SqlSyntaxKind.Query                => PreVisitQuery(node),
            SqlSyntaxKind.SelectClause         => PreVisitSelectClause(node),
            SqlSyntaxKind.FromClause           => PreVisitFromClause(node),
            SqlSyntaxKind.OrderByClause        => PreVisitOrderByClause(node),
            SqlSyntaxKind.ColumnList           => PreVisitColumnList(node),
            SqlSyntaxKind.ColumnExpression     => PreVisitColumnExpression(node),
            SqlSyntaxKind.TableRelation        => PreVisitTableRelation(node),
            SqlSyntaxKind.JoinClause           => PreVisitJoinClause(node),
            SqlSyntaxKind.RawTokens            => PreVisitRawTokens(node),
            SqlSyntaxKind.CaseExpression       => PreVisitCaseExpression(node),
            SqlSyntaxKind.CaseWhenExpression   => PreVisitCaseWhenExpression(node),
            SqlSyntaxKind.CaseElseExpression   => PreVisitCaseElseExpression(node),
            SqlSyntaxKind.Predicate            => PreVisitPredicate(node),
            _                                  => PreVisitDefault(node)
        };

        if (flow == SqlVisitFlow.Break) return SqlVisitFlow.Break;

        // Recurse into child nodes.
        foreach (var e in node.Elements)
        {
            if (e.Kind != SqlElementKind.Node) continue;
            if (Visit(e.Node!) == SqlVisitFlow.Break) return SqlVisitFlow.Break;
        }

        // Post-visit dispatch
        return node.Kind switch
        {
            SqlSyntaxKind.Script               => PostVisitScript(node),
            SqlSyntaxKind.WithClause           => PostVisitWithClause(node),
            SqlSyntaxKind.CommonTableExpression => PostVisitCommonTableExpression(node),
            SqlSyntaxKind.CteBody              => PostVisitCteBody(node),
            SqlSyntaxKind.Query                => PostVisitQuery(node),
            SqlSyntaxKind.SelectClause         => PostVisitSelectClause(node),
            SqlSyntaxKind.FromClause           => PostVisitFromClause(node),
            SqlSyntaxKind.OrderByClause        => PostVisitOrderByClause(node),
            SqlSyntaxKind.ColumnList           => PostVisitColumnList(node),
            SqlSyntaxKind.ColumnExpression     => PostVisitColumnExpression(node),
            SqlSyntaxKind.TableRelation        => PostVisitTableRelation(node),
            SqlSyntaxKind.JoinClause           => PostVisitJoinClause(node),
            SqlSyntaxKind.RawTokens            => PostVisitRawTokens(node),
            SqlSyntaxKind.CaseExpression       => PostVisitCaseExpression(node),
            SqlSyntaxKind.CaseWhenExpression   => PostVisitCaseWhenExpression(node),
            SqlSyntaxKind.CaseElseExpression   => PostVisitCaseElseExpression(node),
            SqlSyntaxKind.Predicate            => PostVisitPredicate(node),
            _                                  => PostVisitDefault(node)
        };
    }

    // -----------------------------------------------------------------------
    // Fallback hooks (called for unrecognized or future node kinds)
    // -----------------------------------------------------------------------

    /// <summary>Called before visiting a node whose kind has no specific override.</summary>
    public virtual SqlVisitFlow PreVisitDefault(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a node whose kind has no specific override.</summary>
    public virtual SqlVisitFlow PostVisitDefault(SqlNode node) => SqlVisitFlow.Continue;

    // -----------------------------------------------------------------------
    // Per-kind pre-visit hooks
    // -----------------------------------------------------------------------

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.Script"/> node.</summary>
    public virtual SqlVisitFlow PreVisitScript(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.WithClause"/> node.</summary>
    public virtual SqlVisitFlow PreVisitWithClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.CommonTableExpression"/> node.</summary>
    public virtual SqlVisitFlow PreVisitCommonTableExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.CteBody"/> node.</summary>
    public virtual SqlVisitFlow PreVisitCteBody(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.Query"/> node.</summary>
    public virtual SqlVisitFlow PreVisitQuery(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.SelectClause"/> node.</summary>
    public virtual SqlVisitFlow PreVisitSelectClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.FromClause"/> node.</summary>
    public virtual SqlVisitFlow PreVisitFromClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.OrderByClause"/> node.</summary>
    public virtual SqlVisitFlow PreVisitOrderByClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.ColumnList"/> node.</summary>
    public virtual SqlVisitFlow PreVisitColumnList(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.ColumnExpression"/> node.</summary>
    public virtual SqlVisitFlow PreVisitColumnExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.TableRelation"/> node.</summary>
    public virtual SqlVisitFlow PreVisitTableRelation(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.JoinClause"/> node.</summary>
    public virtual SqlVisitFlow PreVisitJoinClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.RawTokens"/> node.</summary>
    public virtual SqlVisitFlow PreVisitRawTokens(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.CaseExpression"/> node.</summary>
    public virtual SqlVisitFlow PreVisitCaseExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.CaseWhenExpression"/> node.</summary>
    public virtual SqlVisitFlow PreVisitCaseWhenExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.CaseElseExpression"/> node.</summary>
    public virtual SqlVisitFlow PreVisitCaseElseExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called before visiting a <see cref="SqlSyntaxKind.Predicate"/> node.</summary>
    public virtual SqlVisitFlow PreVisitPredicate(SqlNode node) => SqlVisitFlow.Continue;

    // -----------------------------------------------------------------------
    // Per-kind post-visit hooks
    // -----------------------------------------------------------------------

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.Script"/> node.</summary>
    public virtual SqlVisitFlow PostVisitScript(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.WithClause"/> node.</summary>
    public virtual SqlVisitFlow PostVisitWithClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.CommonTableExpression"/> node.</summary>
    public virtual SqlVisitFlow PostVisitCommonTableExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.CteBody"/> node.</summary>
    public virtual SqlVisitFlow PostVisitCteBody(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.Query"/> node.</summary>
    public virtual SqlVisitFlow PostVisitQuery(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.SelectClause"/> node.</summary>
    public virtual SqlVisitFlow PostVisitSelectClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.FromClause"/> node.</summary>
    public virtual SqlVisitFlow PostVisitFromClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.OrderByClause"/> node.</summary>
    public virtual SqlVisitFlow PostVisitOrderByClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.ColumnList"/> node.</summary>
    public virtual SqlVisitFlow PostVisitColumnList(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.ColumnExpression"/> node.</summary>
    public virtual SqlVisitFlow PostVisitColumnExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.TableRelation"/> node.</summary>
    public virtual SqlVisitFlow PostVisitTableRelation(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.JoinClause"/> node.</summary>
    public virtual SqlVisitFlow PostVisitJoinClause(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.RawTokens"/> node.</summary>
    public virtual SqlVisitFlow PostVisitRawTokens(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.CaseExpression"/> node.</summary>
    public virtual SqlVisitFlow PostVisitCaseExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.CaseWhenExpression"/> node.</summary>
    public virtual SqlVisitFlow PostVisitCaseWhenExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.CaseElseExpression"/> node.</summary>
    public virtual SqlVisitFlow PostVisitCaseElseExpression(SqlNode node) => SqlVisitFlow.Continue;

    /// <summary>Called after visiting a <see cref="SqlSyntaxKind.Predicate"/> node.</summary>
    public virtual SqlVisitFlow PostVisitPredicate(SqlNode node) => SqlVisitFlow.Continue;
}
