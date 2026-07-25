using System.Text;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Turns CEL source into a flat token stream. Never crashes on hostile input: every rejection is a
/// <see cref="CelSyntaxException"/> carrying the offending character's position, never a raw
/// framework exception (<see cref="IndexOutOfRangeException"/>, <see cref="FormatException"/>, …).
/// </summary>
internal static class CelLexer
{
    private static readonly string[] _validContextNames = ["user", "tenant"];

    /// <summary>Tokenizes CEL source into a flat token stream, ending with <see cref="CelTokenKind.EndOfInput"/>.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <exception cref="CelSyntaxException">The source contains a character or sequence the grammar does not allow.</exception>
    public static IReadOnlyList<CelToken> Tokenize(string source)
    {
        var tokens = new List<CelToken>();
        var position = 0;

        while (SkipWhitespace(source, ref position))
        {
            tokens.Add(ReadToken(source, ref position));
        }

        tokens.Add(new CelToken(CelTokenKind.EndOfInput, string.Empty, position));
        return tokens;
    }

    private static bool SkipWhitespace(string source, ref int position)
    {
        while (position < source.Length && char.IsWhiteSpace(source[position]))
        {
            position++;
        }

        return position < source.Length;
    }

    private static CelToken ReadToken(string source, ref int position)
    {
        var current = source[position];

        if (current is '\'' or '"')
        {
            return ReadString(source, ref position);
        }

        if (char.IsDigit(current))
        {
            return ReadNumber(source, ref position);
        }

        if (current == '@')
        {
            return ReadContextReference(source, ref position);
        }

        if (char.IsLetter(current) || current == '_')
        {
            return ReadIdentifierOrKeyword(source, ref position);
        }

        return ReadSymbol(source, ref position);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static CelToken ReadIdentifierOrKeyword(string source, ref int position)
    {
        var start = position;
        while (position < source.Length && IsIdentifierChar(source[position]))
        {
            position++;
        }

        var text = source[start..position];
        return new CelToken(KeywordKind(text) ?? CelTokenKind.Identifier, text, start);
    }

    private static CelTokenKind? KeywordKind(string text) => text switch
    {
        "true" => CelTokenKind.True,
        "false" => CelTokenKind.False,
        "null" => CelTokenKind.Null,
        "in" => CelTokenKind.In,
        "has" => CelTokenKind.Has,
        _ => null,
    };

    private static CelToken ReadContextReference(string source, ref int position)
    {
        var start = position;
        position++;
        var nameStart = position;
        while (position < source.Length && IsIdentifierChar(source[position]))
        {
            position++;
        }

        var name = source[nameStart..position];
        if (!_validContextNames.Contains(name, StringComparer.Ordinal))
        {
            throw new CelSyntaxException($"Unknown context reference '@{name}'.", start);
        }

        return new CelToken(CelTokenKind.ContextReference, name, start);
    }

    private static CelToken ReadNumber(string source, ref int position)
    {
        var start = position;
        ConsumeDigits(source, ref position);

        var isDecimal = false;
        if (HasDecimalPoint(source, position))
        {
            isDecimal = true;
            position++;
            ConsumeDigits(source, ref position);
        }

        if (position < source.Length && source[position] == '.')
        {
            throw new CelSyntaxException("Malformed numeric literal.", start);
        }

        var text = source[start..position];
        return new CelToken(isDecimal ? CelTokenKind.DecimalLiteral : CelTokenKind.IntLiteral, text, start);
    }

    private static bool HasDecimalPoint(string source, int position) =>
        position < source.Length && source[position] == '.'
        && position + 1 < source.Length && char.IsDigit(source[position + 1]);

    private static void ConsumeDigits(string source, ref int position)
    {
        while (position < source.Length && char.IsDigit(source[position]))
        {
            position++;
        }
    }

    private static CelToken ReadString(string source, ref int position)
    {
        var start = position;
        var quote = source[position];
        position++;
        var builder = new StringBuilder();

        while (true)
        {
            RequireMoreCharacters(source, position, start);

            if (source[position] == quote)
            {
                position++;
                break;
            }

            RejectRawNewline(source, position);
            builder.Append(ReadStringChar(source, ref position));
        }

        return new CelToken(CelTokenKind.StringLiteral, builder.ToString(), start);
    }

    private static void RequireMoreCharacters(string source, int position, int start)
    {
        if (position >= source.Length)
        {
            throw new CelSyntaxException("Unterminated string literal.", start);
        }
    }

    private static void RejectRawNewline(string source, int position)
    {
        if (source[position] == '\n')
        {
            throw new CelSyntaxException(
                "A string literal cannot contain a raw newline.",
                position,
                "Escape it instead: \\n.");
        }
    }

    private static char ReadStringChar(string source, ref int position)
    {
        if (source[position] == '\\')
        {
            return ReadEscape(source, ref position);
        }

        return source[position++];
    }

    private static char ReadEscape(string source, ref int position)
    {
        var backslashPosition = position;
        position++;
        if (position >= source.Length)
        {
            throw new CelSyntaxException("Unterminated escape sequence.", backslashPosition);
        }

        var escaped = source[position];
        position++;
        return escaped switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            _ => throw new CelSyntaxException($"Unknown escape sequence '\\{escaped}'.", backslashPosition),
        };
    }

    private static CelToken ReadSymbol(string source, ref int position)
    {
        var start = position;
        return source[position] switch
        {
            '.' => SingleCharToken(source, ref position, CelTokenKind.Dot),
            ',' => SingleCharToken(source, ref position, CelTokenKind.Comma),
            '(' => SingleCharToken(source, ref position, CelTokenKind.LeftParen),
            ')' => SingleCharToken(source, ref position, CelTokenKind.RightParen),
            '[' => SingleCharToken(source, ref position, CelTokenKind.LeftBracket),
            ']' => SingleCharToken(source, ref position, CelTokenKind.RightBracket),
            '?' => SingleCharToken(source, ref position, CelTokenKind.Question),
            ':' => SingleCharToken(source, ref position, CelTokenKind.Colon),
            '+' => SingleCharToken(source, ref position, CelTokenKind.Plus),
            '-' => SingleCharToken(source, ref position, CelTokenKind.Minus),
            '*' => SingleCharToken(source, ref position, CelTokenKind.Star),
            '/' => SingleCharToken(source, ref position, CelTokenKind.Slash),
            '=' => ReadDoubledOrThrow(source, ref position, '=', CelTokenKind.Equal, "=="),
            '<' => ReadOptionallyFollowedByEquals(source, ref position, CelTokenKind.Less, CelTokenKind.LessOrEqual),
            '>' => ReadOptionallyFollowedByEquals(source, ref position, CelTokenKind.Greater, CelTokenKind.GreaterOrEqual),
            '!' => ReadOptionallyFollowedByEquals(source, ref position, CelTokenKind.Not, CelTokenKind.NotEqual),
            '&' => ReadDoubledOrThrow(source, ref position, '&', CelTokenKind.And, "&&"),
            '|' => ReadDoubledOrThrow(source, ref position, '|', CelTokenKind.Or, "||"),
            var unexpected => throw new CelSyntaxException($"Unexpected character '{unexpected}'.", start),
        };
    }

    private static CelToken SingleCharToken(string source, ref int position, CelTokenKind kind)
    {
        var start = position;
        position++;
        return new CelToken(kind, source[start..position], start);
    }

    private static CelToken ReadOptionallyFollowedByEquals(
        string source, ref int position, CelTokenKind aloneKind, CelTokenKind withEqualsKind)
    {
        var start = position;
        if (position + 1 < source.Length && source[position + 1] == '=')
        {
            position += 2;
            return new CelToken(withEqualsKind, source[start..position], start);
        }

        position++;
        return new CelToken(aloneKind, source[start..position], start);
    }

    private static CelToken ReadDoubledOrThrow(
        string source, ref int position, char repeated, CelTokenKind kind, string text)
    {
        var start = position;
        if (position + 1 < source.Length && source[position + 1] == repeated)
        {
            position += 2;
            return new CelToken(kind, text, start);
        }

        throw new CelSyntaxException($"Expected '{text}', found a single '{repeated}'.", start);
    }
}
