namespace SpiritSync.Generators.Parser.SqlNodes;

internal class SqlColumnListNode : SqlNode
{
    public SqlColumnListNode(SqlSyntaxKind kind, SqlNode? parent = null, List<SqlSyntaxElement>? elements = null) : base(kind, parent, elements)
    {
    }

    public List<SqlExpressionNode> Columns { get; set; } = [];

    public override SqlNode Clone()
    {
        var copy = new SqlColumnListNode(Kind, null, new List<SqlSyntaxElement>(Elements.Count));
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node)
            {
                var cloned = e.Node!.Clone();
                copy.Add(cloned);
                if (cloned is SqlExpressionNode expr) copy.Columns.Add(expr);
            }
            else
            {
                copy.Elements.Add(e);
            }
        }
        return copy;
    }
}
