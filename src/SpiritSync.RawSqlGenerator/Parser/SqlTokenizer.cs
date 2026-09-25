// SqlTokenizer.cs

using System.Runtime.CompilerServices;

namespace SpiritSync.Generators.Parser;

/// <summary>
/// A single-pass, allocation-free SQL tokenizer.
/// </summary>
/// <remarks>
/// Instances are inexpensive value types that walk a <see cref="ReadOnlySpan{Char}"/> once from
/// left to right, yielding <see cref="SqlToken"/>s via <see cref="MoveNext"/>. The tokenizer
/// preserves whitespace, newlines, and comments as trivia tokens so that the resulting parse
/// tree can reproduce the input byte-for-byte.
/// <para/>
/// The tokenizer recognizes the small set of T-SQL keywords the <c>RawSqlGenerator</c> needs
/// (see <see cref="SqlKeyword"/>). All other identifier-shaped runs are classified as
/// <see cref="SqlTokenKind.Identifier"/>.
/// </remarks>
internal ref struct SqlTokenizer
{
    private readonly ReadOnlySpan<char> _source;
    private int _position;
    private SqlToken _current;

    public SqlTokenizer(ReadOnlySpan<char> source)
    {
        _source = source;
        _position = 0;
        _current = default;
    }

    /// <summary>The most recently produced token. Only valid after a successful <see cref="MoveNext"/>.</summary>
    public SqlToken Current => _current;

    /// <summary>Consumes the next token from the input. Returns <c>false</c> at end-of-input.</summary>
    public bool MoveNext()
    {
        if (_position >= _source.Length)
        {
            _current = new SqlToken(SqlTokenKind.EndOfFile, _position, 0);
            return false;
        }

        var start = _position;
        var c = _source[_position];

        // Newlines are their own token kind so that line-oriented transformations remain cheap.
        if (c == '\r')
        {
            _position++;
            if (_position < _source.Length && _source[_position] == '\n') _position++;
            _current = new SqlToken(SqlTokenKind.NewLine, start, _position - start);
            return true;
        }
        if (c == '\n')
        {
            _position++;
            _current = new SqlToken(SqlTokenKind.NewLine, start, 1);
            return true;
        }

        if (c == ' ' || c == '\t')
        {
            while (_position < _source.Length)
            {
                var cc = _source[_position];
                if (cc == ' ' || cc == '\t') _position++;
                else break;
            }
            _current = new SqlToken(SqlTokenKind.Whitespace, start, _position - start);
            return true;
        }

        // Comments
        if (c == '-' && _position + 1 < _source.Length && _source[_position + 1] == '-')
        {
            _position += 2;
            while (_position < _source.Length && _source[_position] != '\n' && _source[_position] != '\r') _position++;
            _current = new SqlToken(SqlTokenKind.LineComment, start, _position - start);
            return true;
        }
        if (c == '/' && _position + 1 < _source.Length && _source[_position + 1] == '*')
        {
            _position += 2;
            while (_position + 1 < _source.Length && !(_source[_position] == '*' && _source[_position + 1] == '/'))
                _position++;
            if (_position + 1 < _source.Length) _position += 2; // consume closing */
            else _position = _source.Length;
            _current = new SqlToken(SqlTokenKind.BlockComment, start, _position - start);
            return true;
        }

        // Punctuation
        switch (c)
        {
            case '(': _position++; _current = new SqlToken(SqlTokenKind.OpenParen, start, 1); return true;
            case ')': _position++; _current = new SqlToken(SqlTokenKind.CloseParen, start, 1); return true;
            case ',': _position++; _current = new SqlToken(SqlTokenKind.Comma, start, 1); return true;
            case ';': _position++; _current = new SqlToken(SqlTokenKind.Semicolon, start, 1); return true;
            case '.':
                // A leading '.' followed by a digit is a numeric literal like ".5"
                if (_position + 1 < _source.Length && IsDigit(_source[_position + 1]))
                {
                    ReadNumber();
                    _current = new SqlToken(SqlTokenKind.Number, start, _position - start);
                    return true;
                }
                _position++;
                _current = new SqlToken(SqlTokenKind.Dot, start, 1);
                return true;
            case '*': _position++; _current = new SqlToken(SqlTokenKind.Star, start, 1); return true;
        }

        // Unresolved brace placeholder: {...} with balanced brace counting.
        // These are surfaced opaquely so that FindUnresolvedInterpolations can report them.
        if (c == '{')
        {
            _position++;
            var depth = 1;
            while (_position < _source.Length && depth > 0)
            {
                var cc = _source[_position];
                if (cc == '{') depth++;
                else if (cc == '}') depth--;
                _position++;
                if (depth == 0) break;
            }
            _current = new SqlToken(SqlTokenKind.Placeholder, start, _position - start);
            return true;
        }

        // Single-quoted string literal, with SQL-style '' escaping.
        if (c == '\'')
        {
            _position++;
            while (_position < _source.Length)
            {
                if (_source[_position] == '\'')
                {
                    // '' is an escaped quote; keep going.
                    if (_position + 1 < _source.Length && _source[_position + 1] == '\'')
                    {
                        _position += 2;
                        continue;
                    }
                    _position++;
                    break;
                }
                _position++;
            }
            _current = new SqlToken(SqlTokenKind.StringLiteral, start, _position - start);
            return true;
        }

        // Bracketed identifier [foo bar]
        if (c == '[')
        {
            _position++;
            while (_position < _source.Length && _source[_position] != ']') _position++;
            if (_position < _source.Length) _position++;
            _current = new SqlToken(SqlTokenKind.Identifier, start, _position - start);
            return true;
        }

        // Double-quoted identifier "foo"
        if (c == '"')
        {
            _position++;
            while (_position < _source.Length && _source[_position] != '"') _position++;
            if (_position < _source.Length) _position++;
            _current = new SqlToken(SqlTokenKind.Identifier, start, _position - start);
            return true;
        }

        // Numeric literal
        if (IsDigit(c))
        {
            ReadNumber();
            _current = new SqlToken(SqlTokenKind.Number, start, _position - start);
            return true;
        }

        // Identifier / keyword
        if (IsIdentifierStart(c))
        {
            _position++;
            while (_position < _source.Length && IsIdentifierPart(_source[_position])) _position++;
            var length = _position - start;
            var kw = SqlKeywordTable.Lookup(_source.Slice(start, length));
            _current = kw == SqlKeyword.None
                ? new SqlToken(SqlTokenKind.Identifier, start, length)
                : new SqlToken(SqlTokenKind.Keyword, start, length, kw);
            return true;
        }

        // Fallback: a single "other" punctuation character (=, +, <, >, etc.)
        _position++;
        _current = new SqlToken(SqlTokenKind.Punctuation, start, 1);
        return true;
    }

    private void ReadNumber()
    {
        // Accept optional integer part, optional '.', optional fractional, optional exponent.
        while (_position < _source.Length && IsDigit(_source[_position])) _position++;
        if (_position < _source.Length && _source[_position] == '.')
        {
            _position++;
            while (_position < _source.Length && IsDigit(_source[_position])) _position++;
        }
        if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
        {
            _position++;
            if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-')) _position++;
            while (_position < _source.Length && IsDigit(_source[_position])) _position++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char c) => (uint)(c - '0') <= 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentifierStart(char c) =>
        (uint)((c | 0x20) - 'a') <= 25 || c == '_' || c == '#' || c == '@';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentifierPart(char c) =>
        (uint)((c | 0x20) - 'a') <= 25 || (uint)(c - '0') <= 9 || c == '_' || c == '$' || c == '#' || c == '@';
}
