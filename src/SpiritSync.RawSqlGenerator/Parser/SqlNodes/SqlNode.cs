// SqlNode.cs

using System.Diagnostics;
using System.Text;

// ReSharper disable ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable InvertIf

namespace SpiritSync.Generators.Parser.SqlNodes;

/// <summary>
/// A node in the lossless SQL parse tree produced by <see cref="SqlParser"/>.
/// </summary>
/// <remarks>
/// Each node owns an ordered list of <see cref="SqlSyntaxElement"/>s. An element is either
/// a <see cref="SqlToken"/> (a leaf pointing at a slice of the original source), a child
/// <see cref="SqlNode"/>, or an inserted <see cref="string"/> literal introduced by the
/// <see cref="SqlTransformer"/>. Writing the tree in-order to a <see cref="TextWriter"/>
/// reproduces the original SQL byte-for-byte (unless the tree has been mutated).
/// <para/>
/// Nodes are mutable — this is intentional because the transformer performs targeted
/// in-place edits (replacing the columns in a SELECT clause, removing lines from a FROM
/// clause, etc.). All allocation-heavy work is confined to the transformer; the parser
/// itself does not allocate more than O(N) in the token count.
/// </remarks>
[DebuggerDisplay("SqlNode: {DebuggerDisplayValue,nq}")]
internal class SqlNode
{
    /// <summary>The kind of this node.</summary>
    public SqlSyntaxKind Kind { get; }

    /// <summary>The ordered list of syntax elements that make up this node's contents.</summary>
    public List<SqlSyntaxElement> Elements { get; }

    /// <summary>The parent node in the parse tree, or <c>null</c> if this is the root.</summary>
    public SqlNode? Parent { get; private set; }

    /// <summary>
    /// The number of additional indentation levels this node introduces relative to its parent
    /// scope. Defaults to <c>0</c>. Set to <c>1</c> on nested <see cref="SqlSyntaxKind.Query"/>
    /// nodes (subqueries inside column expressions or EXISTS predicates) so the formatter can
    /// indent their content one extra level beyond the surrounding context.
    /// </summary>
    public int IndentScope { get; set; }

    /// <summary>
    /// The name of the file from which the SQL syntax originated, or <c>null</c> if the source
    /// was not read from a file. Set on the root node by the parser; child nodes delegate to
    /// the root so the value is always consistent across the whole tree.
    /// </summary>
    public string? SqlConstantName
    {
        get => Parent is null ? field : GetRoot()?.SqlConstantName;
        set
        {
            if (Parent is null) field = value;
            else throw new InvalidOperationException("SqlConstantName may only be set on the root node.");
        }
    }

    public SqlNode(SqlSyntaxKind kind, SqlNode? parent = null, List<SqlSyntaxElement>? elements = null)
    {
        Kind = kind;
        Elements = elements ?? new List<SqlSyntaxElement>();
        parent?.Add(this);
    }

    public SqlNode? GetRoot()
    {
        var root = this;
        while (root?.Parent is not null)
            root = root.Parent;
        return root;
    }

    /// <summary>Appends a token element to this node.</summary>
    public void Add(SqlToken token) => Elements.Add(SqlSyntaxElement.FromToken(token));

    /// <summary>Appends a child node element to this node.</summary>
    public void Add(SqlNode node)
    {
        node.Parent = this;
        Elements.Add(SqlSyntaxElement.FromNode(node));
    }

    /// <summary>Appends a literal text element to this node (used by transformations).</summary>
    public void Add(string text) => Elements.Add(SqlSyntaxElement.FromText(text));

    /// <summary>
    /// Writes this node's contents to <paramref name="writer"/> in source order using
    /// <paramref name="sourceChars"/> as the backing character buffer for token slices.
    /// </summary>
    public void Write(TextWriter writer, char[] sourceChars)
    {
        for (var i = 0; i < Elements.Count; i++)
            Elements[i].Write(writer, sourceChars);
    }

    /// <summary>
    /// Materializes this node as a <see cref="string"/> using <paramref name="source"/> as the
    /// backing text for token slices. Convenience overload; allocates one <see cref="char"/>
    /// array for the render pass.
    /// </summary>
    public string ToString(string source)
    {
        var sb = new StringBuilder(EstimateLength());
        var chars = source.ToCharArray();
        using (var sw = new StringWriter(sb))
            Write(sw, chars);
        return sb.ToString();
    }

    /// <summary>Returns a deep clone of this node (the Elements list is copied; child nodes are recursively cloned).
    /// The cloned root has no <see cref="Parent"/>; every cloned child's <see cref="Parent"/> points into the cloned tree,
    /// not the original.</summary>
    public virtual SqlNode Clone()
    {
        var copy = new SqlNode(Kind, null, new List<SqlSyntaxElement>(Elements.Count));
        for (var i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            if (e.Kind == SqlElementKind.Node)
                copy.Add(e.Node!.Clone());
            else
                copy.Elements.Add(e);
        }
        return copy;
    }

    /// <summary>Recursively locates the first descendant node of the given kind, or <c>null</c>.</summary>
    public SqlNode? FirstDescendant(SqlSyntaxKind kind)
    {
        foreach (var e in Elements)
        {
            if (e.Kind == SqlElementKind.Node)
            {
                var child = e.Node!;
                if (child.Kind == kind) return child;
                var deeper = child.FirstDescendant(kind);
                if (deeper is not null) return deeper;
            }
        }
        return null;
    }

    /// <summary>Iterates over direct child nodes of the given kind.</summary>
    public IEnumerable<SqlNode> ChildNodes(SqlSyntaxKind kind)
    {
        foreach (var e in Elements)
        {
            if (e.Kind == SqlElementKind.Node && e.Node!.Kind == kind)
                yield return e.Node!;
        }
    }

    /// <summary>Iterates over every direct child node.</summary>
    public IEnumerable<SqlNode> ChildNodes()
    {
        foreach (var e in Elements)
        {
            if (e.Kind == SqlElementKind.Node) yield return e.Node!;
        }
    }

    /// <summary>Value shown in the debugger display. Overridden by subclasses to include alias or other context.</summary>
    protected virtual string DebuggerDisplayValue => Kind.ToString();

    /// <summary>Returns <c>true</c> if this node (or any descendant) contains a newline token.</summary>
    internal bool ContainsNewLine()
    {
        foreach (var e in Elements)
        {
            if (e.Kind == SqlElementKind.Token && e.Token.Kind == SqlTokenKind.NewLine) return true;
            if (e.Kind == SqlElementKind.Node && e.Node!.ContainsNewLine()) return true;
        }
        return false;
    }

    internal int EstimateLength()
    {
        var total = 0;
        foreach (var e in Elements) total += e.EstimateLength();
        return total < 16 ? 16 : total;
    }
}
