// SqlText.cs

using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpiritSync.Generators.Supplemental;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Text-shaping and interpolation utilities used when preparing SQL for emission as a raw
/// string constant and when auditing SQL for unresolved C# interpolation placeholders.
/// </summary>
/// <remarks>
/// These helpers deal with the SQL string as text (indentation, line splitting, brace
/// scanning). Structural transformations of the SQL — replacing SELECT columns, editing
/// CTEs, and so on — live in <see cref="SqlTransformer"/>.
/// </remarks>
internal static class SqlText
{
    /// <summary>
    /// The indentation prefix applied to each SQL line in the generated raw string literal.
    /// 12 spaces; must match how the closing triple-quote line is emitted.
    /// </summary>
    internal const string GeneratedIndent = "            ";

    /// <summary>
    /// Normalizes indentation of <paramref name="sqlText"/> and re-indents it with
    /// <see cref="GeneratedIndent"/> so it aligns correctly inside a generated raw string literal.
    /// </summary>
    /// <remarks>
    /// Operates directly on <see cref="ReadOnlySpan{Char}"/> slices of the input to avoid
    /// allocating one <see cref="string"/> per line. A single <see cref="StringBuilder"/>
    /// materialises the final result.
    /// </remarks>
    internal static string PrepareSqlForRawString(string sqlText)
    {
        if (string.IsNullOrEmpty(sqlText)) return string.Empty;

        var source = sqlText.AsSpan();

        // Single pass to find line ranges and the minimum indentation among non-blank lines.
        // A "line" here is exclusive of its terminating newline.
        var lineStarts = new List<int>(16);
        var lineEnds = new List<int>(16);
        var minIndent = int.MaxValue;
        var i = 0;
        while (i <= source.Length)
        {
            var start = i;
            while (i < source.Length && source[i] != '\n' && source[i] != '\r') i++;
            var end = i;
            lineStarts.Add(start);
            lineEnds.Add(end);

            // Update min indent if this line is non-blank.
            var indent = 0;
            var isBlank = true;
            for (var k = start; k < end; k++)
            {
                var c = source[k];
                if (c == ' ') { indent++; continue; }
                if (c == '\t') { indent += 4; continue; }
                isBlank = false;
                break;
            }
            if (!isBlank && indent < minIndent) minIndent = indent;

            // Consume the newline (CRLF, LF, CR).
            if (i < source.Length && source[i] == '\r') i++;
            if (i < source.Length && source[i] == '\n') i++;
            if (i == end && end == source.Length) break; // avoid duplicate trailing empty entry
        }
        if (minIndent == int.MaxValue) minIndent = 0;

        // Skip leading and trailing blank lines.
        var firstLine = 0;
        while (firstLine < lineStarts.Count && IsBlank(source, lineStarts[firstLine], lineEnds[firstLine]))
            firstLine++;
        var lastLine = lineStarts.Count - 1;
        while (lastLine >= firstLine && IsBlank(source, lineStarts[lastLine], lineEnds[lastLine]))
            lastLine--;
        if (lastLine < firstLine) return string.Empty;

        // Rebuild: for each retained line, drop up to minIndent leading spaces, then prepend
        // GeneratedIndent, then append a newline. The trailing newline of the final line is
        // trimmed to match the previous behavior (caller adds its own).
        // Reserve capacity assuming ~2 chars per newline; StringBuilder.AppendLine uses
        // Environment.NewLine internally, matching the legacy behavior.
        var sb = new StringBuilder(sqlText.Length + (GeneratedIndent.Length + 2) * (lastLine - firstLine + 1));
        for (var li = firstLine; li <= lastLine; li++)
        {
            var start = lineStarts[li];
            var end = lineEnds[li];
            // Trim minIndent characters from the line, counting tabs as 4 (matches the
            // legacy SqlTextHelpers.PrepareSqlForRawString behavior).
            var trimStart = start;
            var trimmed = 0;
            while (trimStart < end && trimmed < minIndent)
            {
                var c = source[trimStart];
                if (c == ' ') { trimmed++; trimStart++; continue; }
                if (c == '\t') { trimmed += 4; trimStart++; continue; }
                break;
            }
            sb.Append(GeneratedIndent);
            sb.Append(sqlText, trimStart, end - trimStart);
            // The legacy implementation used StringBuilder.AppendLine between every line
            // and then trimmed the trailing newline sequence; we emit AppendLine only
            // between lines to reach the same "no trailing newline" result directly.
            if (li != lastLine) sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strips the common leading whitespace from all non-empty lines of <paramref name="value"/>
    /// so that relative indentation is preserved but absolute indentation is removed.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    internal static string? PreserveRelativeIndent(string? value)
    {
        if (value is null) return null;
        if (value.Length == 0) return value;

        // Fast path: no newline is present at all.
        if (value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0) return value;

        var source = value.AsSpan();
        var lineStarts = new List<int>(8);
        var lineEnds = new List<int>(8);

        var i = 0;
        while (i <= source.Length)
        {
            var start = i;
            while (i < source.Length && source[i] != '\n' && source[i] != '\r') i++;
            lineStarts.Add(start);
            lineEnds.Add(i);
            if (i >= source.Length) break;
            if (source[i] == '\r') i++;
            if (i < source.Length && source[i] == '\n') i++;
            if (i == source.Length)
            {
                // Trailing newline produces an empty final line.
                lineStarts.Add(i);
                lineEnds.Add(i);
                break;
            }
        }

        var minIndent = int.MaxValue;
        for (var li = 0; li < lineStarts.Count; li++)
        {
            var start = lineStarts[li];
            var end = lineEnds[li];
            if (IsBlank(source, start, end)) continue;
            var indent = 0;
            for (var k = start; k < end; k++)
            {
                if (source[k] == ' ' || source[k] == '\t') indent++;
                else break;
            }
            if (indent < minIndent) minIndent = indent;
        }
        if (minIndent <= 0 || minIndent == int.MaxValue) return NormaliseToLf(value);

        var sb = new StringBuilder(value.Length);
        for (var li = 0; li < lineStarts.Count; li++)
        {
            var start = lineStarts[li];
            var end = lineEnds[li];
            var take = end - start;
            if (take >= minIndent) start += minIndent;
            sb.Append(value, start, end - start);
            if (li != lineStarts.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Applies <paramref name="indent"/> spaces of leading whitespace to every line after the first
    /// in a multiline <paramref name="value"/>.
    /// </summary>
    internal static string IndentMultiline(string value, int indent)
    {
        if (string.IsNullOrEmpty(value) || indent <= 0) return value;
        if (value.IndexOf('\n') < 0) return value;

        // Precompute the indent once.
        var indentStr = new string(' ', indent);

        var sb = new StringBuilder(value.Length + indent * 4);
        var source = value.AsSpan();
        var i = 0;
        var atLineStart = false;
        while (i < source.Length)
        {
            var c = source[i];
            if (atLineStart)
            {
                sb.Append(indentStr);
                atLineStart = false;
            }
            sb.Append(c);
            if (c == '\n') atLineStart = true;
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the column-based indentation of the line on which <paramref name="interpolation"/> starts,
    /// counting only leading spaces and tabs up to the opening brace.
    /// </summary>
    internal static int GetRelativeIndent(InterpolationSyntax interpolation)
    {
        var syntaxTree = interpolation.SyntaxTree;
        var text = syntaxTree.GetText();
        var startLine = text.Lines.GetLineFromPosition(interpolation.SpanStart);
        var lineStart = startLine.Start;
        var column = interpolation.SpanStart - lineStart;

        // Count leading spaces/tabs on the line, up to the column where '{' starts.
        var lineText = text.ToString(new Microsoft.CodeAnalysis.Text.TextSpan(lineStart, column));
        var indent = 0;
        foreach (var ch in lineText)
        {
            if (ch == ' ' || ch == '\t') indent++;
            else break;
        }
        return indent;
    }

    /// <summary>
    /// Scans <paramref name="sqlText"/> for unresolved interpolation placeholders of the form
    /// <c>{...}</c>. These may appear outside of SQL string literals or yielding their line, column,
    /// and content.
    /// </summary>
    /// <remarks>
    /// Delegates the string/comment/newline handling to <see cref="SqlTokenizer"/> so that
    /// placeholder detection stays in sync with the parser's view of the world.
    /// The tokenizer produces <see cref="SqlTokenKind.Placeholder"/> tokens exactly where
    /// unresolved braces appear outside quoted strings; we then translate their offsets
    /// into 1-based line / column pairs.
    /// </remarks>
    internal static IEnumerable<(int Line, int Column, string Content)> FindUnresolvedInterpolations(string sqlText)
    {
        // A ref-struct tokenizer cannot cross a yield boundary, so we collect the results
        // eagerly. The list is normally empty (unresolved placeholders are the exception,
        // not the rule), so the extra allocation is negligible in the common case.
        if (string.IsNullOrEmpty(sqlText)) return Array.Empty<(int, int, string)>();
        return CollectUnresolvedInterpolations(sqlText);
    }

    private static List<(int Line, int Column, string Content)> CollectUnresolvedInterpolations(string sqlText)
    {
        var results = new List<(int, int, string)>();
        var tokenizer = new SqlTokenizer(sqlText.AsSpan());
        // Track the current line and the offset of its start, so that column = offset - lineStart.
        var line = 1;
        var lineStart = 0;
        var lastPos = 0;

        while (tokenizer.MoveNext())
        {
            var tok = tokenizer.Current;
            // Update line tracking for any characters between lastPos and tok.Start.
            AdvanceLineCounters(sqlText, lastPos, tok.Start, ref line, ref lineStart);
            lastPos = tok.Start;

            if (tok.Kind == SqlTokenKind.Placeholder)
            {
                AdvanceLineCounters(sqlText, tok.Start, tok.End, ref line, ref lineStart);
                lastPos = tok.End;
                var innerStart = tok.Start + 1;
                var innerLen = Math.Max(0, tok.Length - 2);
                var inner = sqlText.Substring(innerStart, innerLen).Trim();
                // Column is 1-based to match the original SqlTextHelpers behavior, which
                // incremented `column` for every consumed character up to and including '}'.
                var column = tok.End - lineStart;
                results.Add((line, column, inner));
            }
            else if (tok.Kind == SqlTokenKind.NewLine)
            {
                AdvanceLineCounters(sqlText, tok.Start, tok.End, ref line, ref lineStart);
                lastPos = tok.End;
            }
        }
        return results;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool IsBlank(ReadOnlySpan<char> source, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var c = source[i];
            if (c != ' ' && c != '\t') return false;
        }
        return true;
    }

    private static string NormaliseToLf(string value)
    {
        if (value.IndexOf('\r') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < value.Length && value[i + 1] == '\n') i++;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AdvanceLineCounters(string text, int from, int to, ref int line, ref int lineStart)
    {
        for (var i = from; i < to; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
    }
}
