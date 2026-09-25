// SqlToken.cs

namespace SpiritSync.Generators.Parser;

/// <summary>
/// A lexical token produced by <see cref="SqlTokenizer"/>.
/// </summary>
/// <remarks>
/// A token is a lightweight value type: it stores only its <see cref="Kind"/>, optional
/// <see cref="Keyword"/> classification, and its <see cref="Start"/>/<see cref="Length"/>
/// offset into the source string. Its textual content is exposed via <see cref="AsSpan"/>
/// (allocation-free) or <see cref="ToString"/> (allocates a new <see cref="string"/>).
/// <para/>
/// Tokens intentionally do not carry a reference to the source string; that would double
/// their size on 64-bit runtimes. Callers own the source string and pass it back in when
/// they need the token's text.
/// </remarks>
internal readonly struct SqlToken : IEquatable<SqlToken>
{
    public SqlToken(SqlTokenKind kind, int start, int length, SqlKeyword keyword = SqlKeyword.None)
    {
        Kind = kind;
        Keyword = keyword;
        Start = start;
        Length = length;
    }

    /// <summary>The lexical category of the token.</summary>
    public SqlTokenKind Kind { get; }

    /// <summary>
    /// The recognized keyword identity, or <see cref="SqlKeyword.None"/> if the token is
    /// not a keyword or is a keyword the parser does not care about.
    /// </summary>
    public SqlKeyword Keyword { get; }

    /// <summary>The 0-based offset of the first character of this token in the source string.</summary>
    public int Start { get; }

    /// <summary>The number of characters this token spans in the source string.</summary>
    public int Length { get; }

    /// <summary>The exclusive end offset of this token in the source string.</summary>
    public int End => Start + Length;

    /// <summary>Returns the token's characters as a slice of <paramref name="source"/> without allocating.</summary>
    public ReadOnlySpan<char> AsSpan(string source) => source.AsSpan(Start, Length);

    /// <summary>Returns the token's characters as a slice of <paramref name="source"/> without allocating.</summary>
    public ReadOnlySpan<char> AsSpan(ReadOnlySpan<char> source) => source.Slice(Start, Length);

    /// <summary>Returns the token's characters as a newly allocated <see cref="string"/>.</summary>
    public string ToString(string source) => source.Substring(Start, Length);

    /// <summary>Returns true if this token is a whitespace / newline / comment token (i.e. trivia).</summary>
    public bool IsTrivia =>
        Kind == SqlTokenKind.Whitespace ||
        Kind == SqlTokenKind.NewLine ||
        Kind == SqlTokenKind.LineComment ||
        Kind == SqlTokenKind.BlockComment;

    /// <summary>Returns true if this token is the specified keyword.</summary>
    public bool IsKeyword(SqlKeyword keyword) => Kind == SqlTokenKind.Keyword && Keyword == keyword;

    public bool Equals(SqlToken other) =>
        Kind == other.Kind && Keyword == other.Keyword && Start == other.Start && Length == other.Length;

    public override bool Equals(object? obj) => obj is SqlToken t && Equals(t);

    public override int GetHashCode()
    {
        unchecked
        {
            var h = (int)Kind;
            h = (h * 397) ^ (int)Keyword;
            h = (h * 397) ^ Start;
            h = (h * 397) ^ Length;
            return h;
        }
    }

    public override string ToString() => $"{Kind}({Keyword}) @ [{Start}..{End})";
}
