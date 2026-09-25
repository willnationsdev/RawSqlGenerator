namespace SpiritSync.Generators.Parser.SqlNodes;

internal class SqlSelectNode : SqlNode
{
    public SqlSelectNode(SqlSyntaxKind kind, SqlNode? parent = null, List<SqlSyntaxElement>? elements = null) : base(kind, parent, elements)
    {
    }

    public override SqlNode Clone()
    {
        var copy = new SqlSelectNode(Kind, null, new List<SqlSyntaxElement>(Elements.Count));
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node) copy.Add(e.Node!.Clone());
            else copy.Elements.Add(e);
        }
        return copy;
    }
}
