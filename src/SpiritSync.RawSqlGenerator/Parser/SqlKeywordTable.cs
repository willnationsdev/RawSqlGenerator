// SqlKeywordTable.cs

using System.Runtime.CompilerServices;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Case-insensitive lookup from an identifier <see cref="ReadOnlySpan{Char}"/> to <see cref="SqlKeyword"/>.
/// </summary>
/// <remarks>
/// The lookup uses a handwritten per-length ASCII switch rather than a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
/// so that it can operate directly on the tokenizer's <c>ReadOnlySpan&lt;char&gt;</c> slice
/// without allocating an interned <see cref="string"/> for every identifier scan.
/// </remarks>
internal static class SqlKeywordTable
{
    /// <summary>
    /// Returns the matching <see cref="SqlKeyword"/> for <paramref name="span"/>, or
    /// <see cref="SqlKeyword.None"/> if <paramref name="span"/> is not a recognized keyword.
    /// </summary>
    public static SqlKeyword Lookup(ReadOnlySpan<char> span)
    {
        // Pre-filter by length to avoid unnecessary comparisons.
        switch (span.Length)
        {
            case 2:
                if (Eq(span, "AS")) return SqlKeyword.As;
                if (Eq(span, "BY")) return SqlKeyword.By;
                if (Eq(span, "ON")) return SqlKeyword.On;
                if (Eq(span, "OR")) return SqlKeyword.Or;
                break;
            case 3:
                if (Eq(span, "AND")) return SqlKeyword.And;
                if (Eq(span, "ALL")) return SqlKeyword.All;
                if (Eq(span, "END")) return SqlKeyword.End;
                if (Eq(span, "NOT")) return SqlKeyword.Not;
                if (Eq(span, "SET")) return SqlKeyword.Set;
                if (Eq(span, "TOP")) return SqlKeyword.Top;
                break;
            case 4:
                if (Eq(span, "CASE")) return SqlKeyword.Case;
                if (Eq(span, "ELSE")) return SqlKeyword.Else;
                if (Eq(span, "FROM")) return SqlKeyword.From;
                if (Eq(span, "FULL")) return SqlKeyword.Full;
                if (Eq(span, "INTO")) return SqlKeyword.Into;
                if (Eq(span, "JOIN")) return SqlKeyword.Join;
                if (Eq(span, "LEFT")) return SqlKeyword.Left;
                if (Eq(span, "THEN")) return SqlKeyword.Then;
                if (Eq(span, "WHEN")) return SqlKeyword.When;
                if (Eq(span, "WITH")) return SqlKeyword.With;
                break;
            case 5:
                if (Eq(span, "APPLY")) return SqlKeyword.Apply;
                if (Eq(span, "CROSS")) return SqlKeyword.Cross;
                if (Eq(span, "FETCH")) return SqlKeyword.Fetch;
                if (Eq(span, "GROUP")) return SqlKeyword.Group;
                if (Eq(span, "INNER")) return SqlKeyword.Inner;
                if (Eq(span, "MERGE")) return SqlKeyword.Merge;
                if (Eq(span, "ORDER")) return SqlKeyword.Order;
                if (Eq(span, "OUTER")) return SqlKeyword.Outer;
                if (Eq(span, "RIGHT")) return SqlKeyword.Right;
                if (Eq(span, "UNION")) return SqlKeyword.Union;
                if (Eq(span, "WHERE")) return SqlKeyword.Where;
                break;
            case 6:
                if (Eq(span, "DELETE")) return SqlKeyword.Delete;
                if (Eq(span, "EXCEPT")) return SqlKeyword.Except;
                if (Eq(span, "EXISTS")) return SqlKeyword.Exists;
                if (Eq(span, "HAVING")) return SqlKeyword.Having;
                if (Eq(span, "INSERT")) return SqlKeyword.Insert;
                if (Eq(span, "OFFSET")) return SqlKeyword.Offset;
                if (Eq(span, "SELECT")) return SqlKeyword.Select;
                if (Eq(span, "UPDATE")) return SqlKeyword.Update;
                if (Eq(span, "VALUES")) return SqlKeyword.Values;
                break;
            case 8:
                if (Eq(span, "DISTINCT")) return SqlKeyword.Distinct;
                break;
            case 9:
                if (Eq(span, "INTERSECT")) return SqlKeyword.Intersect;
                break;
        }
        return SqlKeyword.None;
    }

    /// <summary>Returns true if <paramref name="span"/> matches <paramref name="upperAscii"/> case-insensitively (ASCII only).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Eq(ReadOnlySpan<char> span, string upperAscii)
    {
        // Callers guarantee the length will match; verify defensively.
        if (span.Length != upperAscii.Length) return false;
        for (var i = 0; i < span.Length; i++)
        {
            var a = span[i];
            // Uppercase ASCII letters via bit-mask; safe because the table only stores A..Z.
            var au = (char)(a & 0xFFDF);
            if (au != upperAscii[i]) return false;
        }
        return true;
    }
}
