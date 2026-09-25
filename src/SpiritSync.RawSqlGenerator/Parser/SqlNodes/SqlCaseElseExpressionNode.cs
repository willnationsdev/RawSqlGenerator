using System.Diagnostics;

namespace SpiritSync.Generators.Parser.SqlNodes;

/// <summary>
/// The <c>ELSE result</c> branch within a <see cref="SqlCaseExpressionNode"/>.
/// </summary>
[DebuggerDisplay("CaseElse")]
internal class SqlCaseElseExpressionNode : SqlNode
{
    public SqlCaseElseExpressionNode(SqlNode? parent = null) : base(SqlSyntaxKind.CaseElseExpression, parent) { }

    public override SqlNode Clone()
    {
        var copy = new SqlCaseElseExpressionNode();
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node) copy.Add(e.Node!.Clone());
            else copy.Elements.Add(e);
        }
        return copy;
    }
}
