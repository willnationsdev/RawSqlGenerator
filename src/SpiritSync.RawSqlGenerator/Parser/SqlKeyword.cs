// SqlKeyword.cs
namespace SpiritSync.Generators.Parser;

/// <summary>
/// Identifies a specific SQL keyword recognised by the tokenizer.
/// </summary>
/// <remarks>
/// Storing the recognised keyword as an enum on <see cref="SqlToken"/> lets the parser
/// dispatch on keyword identity via cheap integer comparisons rather than repeated
/// case-insensitive string matches, which is critical for the "minimal allocations,
/// minimal iterations" goal of the parsing API.
/// <para/>
/// Only keywords that the parser/transformer actually reason about are enumerated.
/// Any other identifier-shaped token is classified as <see cref="SqlTokenKind.Identifier"/>.
/// </remarks>
internal enum SqlKeyword : byte
{
    /// <summary>Not a recognised keyword.</summary>
    None,

    With,
    Select,
    From,
    Where,
    Order,
    By,
    Offset,
    Fetch,
    Group,
    Having,
    Union,
    Intersect,
    Except,
    As,
    Inner,
    Left,
    Right,
    Full,
    Outer,
    Cross,
    Join,
    Apply,
    On,
    Distinct,
    Top,
    All,
    And,
    Or,
    Not,
    Case,
    When,
    Then,
    Else,
    End,
    Insert,
    Update,
    Delete,
    Merge,
    Into,
    Values,
    Set,
    Exists,
}
