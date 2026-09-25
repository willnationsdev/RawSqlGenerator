using System.Diagnostics;

namespace SpiritSync.Generators.Parser.SqlNodes;

/// <summary>
/// A predicate expression (e.g. the condition in a <c>WHEN</c> clause).
/// <para>
/// A predicate node is either a <em>leaf</em> (no <see cref="Children"/>) or a
/// <em>composite</em> node whose <see cref="Children"/> are joined by <see cref="Operator"/>.
/// </para>
/// <list type="bullet">
///   <item>Leaf nodes hold the raw tokens for a single atomic predicate and, when the
///   predicate is <c>EXISTS(SELECT …)</c>, expose the parsed sub-query via
///   <see cref="ExistsQuery"/>.</item>
///   <item>Composite nodes have a non-null <see cref="Operator"/> (<c>"AND"</c> or
///   <c>"OR"</c>) and a <see cref="Children"/> list of two-or-more child
///   <see cref="SqlPredicateNode"/> instances.</item>
/// </list>
/// <see cref="PredicateText"/> always renders the full predicate with any
/// <c>EXISTS(…)</c> bodies collapsed to <c>EXISTS(...)</c>.
/// </summary>
[DebuggerDisplay("Predicate: {PredicateText,nq}")]
internal class SqlPredicateNode : SqlNode
{
    public SqlPredicateNode(SqlNode? parent = null) : base(SqlSyntaxKind.Predicate, parent) { }

    /// <summary>
    /// The predicate text with any EXISTS sub-query bodies replaced by <c>EXISTS(...)</c>.
    /// Suitable for display in the debugger without expanding nested SELECT statements.
    /// </summary>
    public string PredicateText { get; set; } = string.Empty;

    /// <summary>
    /// For composite predicates: the joining operator between <see cref="Children"/>
    /// (<c>"AND"</c> or <c>"OR"</c>). <c>null</c> for leaf predicates.
    /// </summary>
    public string? Operator { get; set; }

    /// <summary>
    /// For composite predicates: the child <see cref="SqlPredicateNode"/> instances
    /// joined by <see cref="Operator"/>. Empty for leaf predicates.
    /// </summary>
    public List<SqlPredicateNode> Children { get; } = new();

    /// <summary>
    /// For leaf predicates that contain <c>EXISTS(SELECT …)</c>: the parsed
    /// <see cref="SqlSyntaxKind.Query"/> sub-node. <c>null</c> otherwise.
    /// </summary>
    public SqlNode? ExistsQuery { get; set; }

    public override SqlNode Clone()
    {
        var copy = new SqlPredicateNode() { PredicateText = PredicateText, Operator = Operator };
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node)
            {
                var cloned = e.Node!.Clone();
                copy.Add(cloned);
                if (cloned is SqlPredicateNode cp)
                    copy.Children.Add(cp);
                else if (cloned.Kind == SqlSyntaxKind.Query && copy.ExistsQuery is null)
                    copy.ExistsQuery = cloned;
            }
            else copy.Elements.Add(e);
        }
        return copy;
    }
}
