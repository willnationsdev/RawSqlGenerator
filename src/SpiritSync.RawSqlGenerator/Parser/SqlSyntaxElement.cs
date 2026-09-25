// SqlSyntaxElement.cs

using System.Diagnostics;
using SpiritSync.Generators.Parser.SqlNodes;

namespace SpiritSync.Generators.Parser;

/// <summary>Discriminator for the three shapes an <see cref="SqlSyntaxElement"/> can take.</summary>
internal enum SqlElementKind : byte
{
    /// <summary>The element is a leaf <see cref="SqlToken"/> pointing into the source string.</summary>
    Token,

    /// <summary>The element is a child <see cref="SqlNode"/>.</summary>
    Node,

    /// <summary>The element is a raw <see cref="string"/> inserted by a transformation.</summary>
    Text,
}

/// <summary>
/// One position in a <see cref="SqlNode.Elements"/> list. Either a leaf token, a child node,
/// or a literal text run introduced by a transformation.
/// </summary>
/// <remarks>
/// Modeled as a small value type with a discriminator so that the parser and transformer
/// can freely intermix all three without a wrapper allocation per element (only the
/// containing list allocates).
/// </remarks>
[DebuggerDisplay("{DebuggerDisplayValue,nq}")]
internal readonly struct SqlSyntaxElement
{
    public SqlElementKind Kind { get; }
    public SqlToken Token { get; }
    public SqlNode? Node { get; }
    public string? Text { get; }

    private SqlSyntaxElement(SqlElementKind kind, SqlToken token, SqlNode? node, string? text)
    {
        Kind = kind;
        Token = token;
        Node = node;
        Text = text;
    }

    public static SqlSyntaxElement FromToken(SqlToken token) => new(SqlElementKind.Token, token, null, null);

    public static SqlSyntaxElement FromNode(SqlNode node) => new(SqlElementKind.Node, default, node, null);

    public static SqlSyntaxElement FromText(string text) => new(SqlElementKind.Text, default, null, text);

    private string DebuggerDisplayValue
    {
        get
        {
            if (Kind != SqlElementKind.Node || Node is null)
                return Kind.ToString();
            var alias = Node switch
            {
                SqlExpressionNode expr => expr.Alias,
                SqlRelationNode rel   => rel.Alias,
                _                     => null,
            };
            return alias is not null
                ? $"SqlSyntaxElement({Node.Kind}): {alias}"
                : $"SqlSyntaxElement({Node.Kind})";
        }
    }

    /// <summary>
    /// Writes this element to <paramref name="writer"/>, resolving token spans against the
    /// shared <paramref name="sourceChars"/> character buffer (which must be the character
    /// content of the source string the tree was parsed from).
    /// </summary>
    public void Write(TextWriter writer, char[] sourceChars)
    {
        switch (Kind)
        {
            case SqlElementKind.Token:
                // TextWriter.Write(char[], int, int) does not allocate; the char[] is shared
                // for the entire render call so that token writes are zero-allocation.
                if (Token.Length > 0) writer.Write(sourceChars, Token.Start, Token.Length);
                break;
            case SqlElementKind.Node:
                Node!.Write(writer, sourceChars);
                break;
            case SqlElementKind.Text:
                if (Text is { Length: > 0 }) writer.Write(Text);
                break;
        }
    }

    internal int EstimateLength() => Kind switch
    {
        SqlElementKind.Token => Token.Length,
        SqlElementKind.Node => Node!.EstimateLength(),
        SqlElementKind.Text => Text?.Length ?? 0,
        _ => 0,
    };
}
