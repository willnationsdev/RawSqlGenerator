using System.Diagnostics;

namespace SpiritSync.Generators.Parser.SqlNodes;

/// <summary>
/// A <c>CASE WHEN … THEN … [ELSE …] END</c> expression node.
/// Child nodes are <see cref="SqlCaseWhenExpressionNode"/> instances (one per WHEN branch)
/// and an optional <see cref="SqlCaseElseExpressionNode"/>.
/// </summary>
[DebuggerDisplay("CaseExpression: {WhenClauses.Count} WHEN(s){(ElseClause != null ? \", ELSE\" : \"\"),nq}")]
internal class SqlCaseExpressionNode : SqlNode
{
    public SqlCaseExpressionNode(SqlNode? parent = null) : base(SqlSyntaxKind.CaseExpression, parent) { }

    public List<SqlCaseWhenExpressionNode> WhenClauses { get; } = new();
    public SqlCaseElseExpressionNode? ElseClause { get; set; }

    public override SqlNode Clone()
    {
        var copy = new SqlCaseExpressionNode();
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node)
            {
                var cloned = e.Node!.Clone();
                copy.Add(cloned);
                if (cloned is SqlCaseWhenExpressionNode w) copy.WhenClauses.Add(w);
                else if (cloned is SqlCaseElseExpressionNode el) copy.ElseClause = el;
            }
            else copy.Elements.Add(e);
        }
        return copy;
    }
}
