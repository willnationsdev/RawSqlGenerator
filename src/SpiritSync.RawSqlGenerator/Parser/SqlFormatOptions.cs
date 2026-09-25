// SqlFormatOptions.cs

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Holds all configurable formatting options that control how a <see cref="SqlFormatter"/>
/// applies presentational changes to SQL text (indentation and spacing normalization).
/// </summary>
internal sealed class SqlFormatOptions
{
    /// <summary>
    /// When <c>true</c> (the default), each column expression in an <c>ORDER BY</c> clause
    /// is placed on its own line. Continuation lines are indented to align with the first
    /// column — i.e. by <c>"ORDER BY ".Length</c> (9 spaces) relative to the <c>ORDER</c>
    /// keyword.
    /// </summary>
    public bool SplitOrderByColumns { get; set; } = true;

    /// <summary>
    /// When <c>true</c> (the default), a parenthesized subquery in a <c>SELECT</c> column list
    /// that starts with <c>(SELECT</c> is reformatted so that the opening <c>(</c> is the last
    /// character on its line, the subquery content is indented by one additional level (4 spaces),
    /// and the closing <c>)</c> appears on its own line at the original column-list indentation.
    /// </summary>
    public bool NormalizeSubqueryIndentation { get; set; } = true;

    /// <summary>
    /// The number of spaces to use for one indentation level when
    /// <see cref="NormalizeSubqueryIndentation"/> is active. Defaults to 4.
    /// </summary>
    public int IndentSize { get; set; } = 4;

    /// <summary>
    /// Maximum length (in characters) of the entire <c>(SELECT ...)</c> subquery, measured
    /// as a single collapsed line, below which the subquery is left inline rather than
    /// being expanded to multi-line format. Defaults to 70.
    /// </summary>
    public int InlineSubqueryLengthThreshold { get; set; } = 70;

    /// <summary>
    /// When <c>true</c> (the default), top-level <c>AND</c> and <c>OR</c> continuation
    /// lines inside a <c>WHERE</c> clause are re-indented so that the right edge of the
    /// keyword aligns with the right edge of <c>WHERE</c>. <c>AND</c> is indented by
    /// <c>WHERE_indent + 2</c> spaces; <c>OR</c> by <c>WHERE_indent + 3</c> spaces.
    /// This alignment is only applied when the <c>AND</c>/<c>OR</c> is at the top level
    /// of the clause — not when it is nested inside parentheses.
    /// </summary>
    public bool AlignWhereAndOrContinuations { get; set; } = true;
}
