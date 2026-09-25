using System.Diagnostics;

namespace SpiritSync.Generators.Parser.SqlNodes;

/// <summary>
/// A single <c>WHEN predicate THEN result</c> branch within a <see cref="SqlCaseExpressionNode"/>.
/// </summary>
[DebuggerDisplay("CaseWhen: {Predicate.PredicateText,nq}")]
internal class SqlCaseWhenExpressionNode : SqlNode
{
    public SqlCaseWhenExpressionNode(SqlNode? parent = null) : base(SqlSyntaxKind.CaseWhenExpression, parent) { }

    /// <summary>The structured predicate for this WHEN clause.</summary>
    public SqlPredicateNode? Predicate { get; set; }

    public override SqlNode Clone()
    {
        var copy = new SqlCaseWhenExpressionNode();
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node)
            {
                var cloned = e.Node!.Clone();
                copy.Add(cloned);
                if (cloned is SqlPredicateNode p) copy.Predicate = p;
            }
            else copy.Elements.Add(e);
        }
        return copy;
    }
}
