using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>
/// A strict RFC 8259 reader of a descriptor's text into a <see cref="JsonNode"/> tree — the generator's own, because an
/// analyzer cannot carry System.Text.Json next to the copy its compiler host already loaded.
/// </summary>
internal static class DescriptorJson
{
    internal const int MaxDepth = 256;

    /// <summary>Reads <paramref name="text"/>, a leading byte order mark skipped.</summary>
    /// <exception cref="DescriptorJsonException">The text is not one JSON value.</exception>
    internal static JsonNode Parse(string text) => new Reader(text).Document();

    private sealed class Reader(string text)
    {
        private int _position = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        private int _depth;

        internal JsonNode Document()
        {
            var root = Value();
            SkipWhitespace();

            return _position == text.Length ? root : throw Error("there is more text after the document's value");
        }

        private JsonNode Value()
        {
            SkipWhitespace();

            return Peek() switch
            {
                '{' => Nested(Object),
                '[' => Nested(Array),
                '"' => new JsonString(String()),
                't' => Literal("true", new JsonBool(true)),
                'f' => Literal("false", new JsonBool(false)),
                'n' => Literal("null", JsonNull.Instance),
                '-' or (>= '0' and <= '9') => Number(),
                '\0' when _position >= text.Length => throw Error("the document ends where a value was expected"),
                var other => throw Error($"'{other}' cannot start a value"),
            };
        }

        private JsonNode Nested(Func<JsonNode> read)
        {
            if (++_depth > MaxDepth)
            {
                throw Error($"the document nests deeper than {MaxDepth} levels");
            }

            var node = read();
            _depth--;

            return node;
        }

        private JsonObject Object()
        {
            _position++;
            var members = ImmutableArray.CreateBuilder<JsonMember>();
            SkipWhitespace();
            if (!TryConsume('}'))
            {
                do
                {
                    members.Add(Member());
                }
                while (Separator('}'));
            }

            return new(members.ToImmutable());
        }

        private JsonMember Member()
        {
            SkipWhitespace();
            if (Peek() != '"')
            {
                throw Error("a property name in double quotes was expected");
            }

            var key = String();
            SkipWhitespace();

            return TryConsume(':') ? new(key, Value()) : throw Error("':' was expected after a property name");
        }

        private JsonArray Array()
        {
            _position++;
            var items = ImmutableArray.CreateBuilder<JsonNode>();
            SkipWhitespace();
            if (!TryConsume(']'))
            {
                do
                {
                    items.Add(Value());
                }
                while (Separator(']'));
            }

            return new(items.ToImmutable());
        }

        private bool Separator(char close)
        {
            SkipWhitespace();
            if (TryConsume(','))
            {
                return true;
            }

            return TryConsume(close) ? false : throw Error($"',' or '{close}' was expected");
        }

        private string String()
        {
            var start = ++_position;
            StringBuilder? unescaped = null;
            while (Peek() != '"')
            {
                if (Peek() == '\\')
                {
                    unescaped ??= new StringBuilder();
                    unescaped.Append(text, start, _position - start).Append(Escape());
                    start = _position;
                }
                else
                {
                    Plain();
                }
            }

            var tail = text.Substring(start, _position++ - start);

            return unescaped is null ? tail : unescaped.Append(tail).ToString();
        }

        private void Plain()
        {
            if (_position >= text.Length)
            {
                throw Error("the string is not terminated");
            }

            _position = text[_position] < ' ' ? throw Error("a control character inside a string must be escaped") : _position + 1;
        }

        private char Escape()
        {
            _position++;
            var escaped = Peek();
            _position++;

            return escaped switch
            {
                '"' or '\\' or '/' => escaped,
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => Unicode(),
                _ => throw Error($"'\\{escaped}' is not a JSON escape", _position - 2),
            };
        }

        private char Unicode()
        {
            if (_position + 4 > text.Length
                || !int.TryParse(text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            {
                throw Error("'\\u' must be followed by four hexadecimal digits");
            }

            _position += 4;

            return (char)code;
        }

        private JsonNumber Number()
        {
            var start = _position;
            TryConsume('-');
            if (!TryConsume('0'))
            {
                Digits("a digit");
            }

            if (TryConsume('.'))
            {
                Digits("a digit after the decimal point");
            }

            if (TryConsume('e') || TryConsume('E'))
            {
                _ = TryConsume('+') || TryConsume('-');
                Digits("a digit in the exponent");
            }

            return new(text.Substring(start, _position - start));
        }

        private void Digits(string expected)
        {
            if (!IsDigit(Peek()))
            {
                throw Error($"{expected} was expected");
            }

            while (IsDigit(Peek()))
            {
                _position++;
            }
        }

        private JsonNode Literal(string literal, JsonNode value)
        {
            if (string.CompareOrdinal(text, _position, literal, 0, literal.Length) != 0)
            {
                throw Error($"'{literal}' was expected");
            }

            _position += literal.Length;

            return value;
        }

        private void SkipWhitespace()
        {
            while (Peek() is ' ' or '\t' or '\n' or '\r')
            {
                _position++;
            }
        }

        private bool TryConsume(char expected)
        {
            if (Peek() != expected)
            {
                return false;
            }

            _position++;

            return true;
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private DescriptorJsonException Error(string message) => Error(message, _position);

        private DescriptorJsonException Error(string message, int position)
        {
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < position && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return new DescriptorJsonException(message, line, position - lineStart + 1);
        }
    }
}
