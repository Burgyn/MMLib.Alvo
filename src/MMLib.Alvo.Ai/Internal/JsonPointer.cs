using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>An RFC 6901 JSON Pointer: <c>""</c> for the whole document, otherwise <c>/</c>-separated tokens.</summary>
/// <remarks>
/// The validator reports its findings at pointers of the same grammar, which is why a violation can name the patch
/// operation that caused it.
/// </remarks>
internal sealed partial class JsonPointer
{
    private const char Separator = '/';

    private JsonPointer(string text, IReadOnlyList<string> tokens)
    {
        Text = text;
        Tokens = tokens;
    }

    /// <summary>The pointer to the whole document.</summary>
    internal static JsonPointer Root { get; } = new(string.Empty, []);

    /// <summary>The pointer as written, escapes included.</summary>
    internal string Text { get; }

    /// <summary>The unescaped reference tokens.</summary>
    internal IReadOnlyList<string> Tokens { get; }

    /// <summary>Whether this is the pointer to the whole document.</summary>
    internal bool IsRoot => Tokens.Count == 0;

    /// <summary>The last reference token; only for a pointer that is not <see cref="Root"/>.</summary>
    internal string Last => Tokens[^1];

    /// <summary>The pointer to the container of what this one addresses; only for a pointer that is not <see cref="Root"/>.</summary>
    internal JsonPointer Parent => new(Text[..Text.LastIndexOf(Separator)], [.. Tokens.Take(Tokens.Count - 1)]);

    /// <summary>This pointer extended by one unescaped token.</summary>
    internal JsonPointer Append(string token) => new($"{Text}{Separator}{Escape(token)}", [.. Tokens, token]);

    /// <summary>Whether <paramref name="other"/> addresses something strictly inside what this pointer addresses.</summary>
    internal bool IsProperPrefixOf(JsonPointer other) =>
        other.Tokens.Count > Tokens.Count
        && Tokens.SequenceEqual(other.Tokens.Take(Tokens.Count), StringComparer.Ordinal);

    /// <summary>Parses <paramref name="text"/>, refusing a pointer that neither is empty nor starts with <c>/</c>.</summary>
    internal static bool TryParse(string? text, [NotNullWhen(true)] out JsonPointer? pointer)
    {
        pointer = null;
        if (text is null || (text.Length > 0 && text[0] != Separator) || InvalidEscape().IsMatch(text))
        {
            return false;
        }

        pointer = text.Length == 0 ? Root : new JsonPointer(text, [.. text[1..].Split(Separator).Select(Unescape)]);
        return true;
    }

    private static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static string Escape(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    [GeneratedRegex("~(?![01])", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidEscape();
}
