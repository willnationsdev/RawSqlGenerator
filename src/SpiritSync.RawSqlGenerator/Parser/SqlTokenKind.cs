// SqlTokenKind.cs
namespace SpiritSync.Generators.Parser;

/// <summary>
/// The kind of lexical token produced by <see cref="SqlTokenizer"/>.
/// </summary>
/// <remarks>
/// The tokenizer is intentionally minimal: it distinguishes only the categories that the
/// <see cref="SqlParser"/> and <see cref="SqlTransformer"/> need in order to build a lossless
/// parse tree suitable for the <c>RawSqlGenerator</c>'s needs. Trivia (whitespace / comments)
/// are surfaced as first-class tokens so that the parse tree can reproduce the original
/// SQL text byte-for-byte.
/// </remarks>
internal enum SqlTokenKind : byte
{
    /// <summary>End-of-input sentinel.</summary>
    EndOfFile,

    /// <summary>Run of ASCII whitespace characters (spaces, tabs, CR, LF).</summary>
    Whitespace,

    /// <summary>A single newline (LF or CRLF), useful when transformations are line-oriented.</summary>
    NewLine,

    /// <summary>A <c>--</c>-style line comment (excluding the terminating newline).</summary>
    LineComment,

    /// <summary>A <c>/* ... */</c>-style block comment.</summary>
    BlockComment,

    /// <summary>A T-SQL keyword (e.g. <c>SELECT</c>, <c>FROM</c>, <c>WITH</c>, <c>AS</c>, ...).</summary>
    Keyword,

    /// <summary>Any non-keyword identifier (unquoted, bracketed, or double-quoted).</summary>
    Identifier,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A <c>'...'</c> single-quoted string literal.</summary>
    StringLiteral,

    /// <summary>The <c>(</c> punctuation.</summary>
    OpenParen,

    /// <summary>The <c>)</c> punctuation.</summary>
    CloseParen,

    /// <summary>The <c>,</c> punctuation.</summary>
    Comma,

    /// <summary>The <c>;</c> statement terminator.</summary>
    Semicolon,

    /// <summary>The <c>.</c> member accessor.</summary>
    Dot,

    /// <summary>The <c>*</c> wildcard / multiplication operator.</summary>
    Star,

    /// <summary>Any other punctuation or operator character (single char).</summary>
    Punctuation,

    /// <summary>
    /// An unresolved brace-delimited placeholder such as <c>{expression}</c> that survived
    /// the C# interpolation-evaluation phase. These are treated opaquely by the parser.
    /// </summary>
    Placeholder,
}
