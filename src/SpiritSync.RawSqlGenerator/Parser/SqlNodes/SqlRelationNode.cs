using System.Diagnostics;

namespace SpiritSync.Generators.Parser.SqlNodes;

[DebuggerDisplay("SqlNode: {DebuggerDisplayValue,nq}")]
internal class SqlRelationNode : SqlNode
{
    public SqlRelationNode(SqlSyntaxKind kind, SqlNode? parent = null, List<SqlSyntaxElement>? elements = null) : base(kind, parent, elements)
    {
    }

    /// <summary>
    /// The alias text for this relation, or <c>null</c> if no alias was detected.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>Returns <see cref="Alias"/>, or <c>null</c> if no alias was detected. The <paramref name="source"/> parameter is unused and kept for API compatibility.</summary>
    public string? ResolveAlias(string source) => Alias;

    protected override string DebuggerDisplayValue => Alias is not null ? $"{Kind}: {Alias}" : Kind.ToString();

    public JoinType JoinType { get; set; }

    public override SqlNode Clone()
    {
        var copy = new SqlRelationNode(Kind, null, new List<SqlSyntaxElement>(Elements.Count)) { Alias = Alias, JoinType = JoinType };
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node) copy.Add(e.Node!.Clone());
            else copy.Elements.Add(e);
        }
        return copy;
    }
}
