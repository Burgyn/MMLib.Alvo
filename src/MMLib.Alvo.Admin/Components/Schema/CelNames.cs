using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Renames one field inside a CEL expression of its own entity — the column references and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token by token, not by word boundary.</b> In an entity's own rules, computed and hook conditions a field is a
/// bare name (<c>owner_id == @user.id</c>) or a member of a row image (<c>new.status</c>, <c>old.status</c>). A
/// word-boundary replace also rewrites string literals — <c>'manager' in @user.roles</c> for a field called
/// <c>manager</c> — and members of <c>@user</c>. So strings are copied whole, a name after <c>@</c> or after a
/// member dot of anything but <c>new</c>/<c>old</c> is left alone, a name followed by <c>(</c> is a function, and a name
/// followed by <c>.</c> is a namespace or an image (<c>math.round</c>, <c>new.</c>), never a column.
/// </para>
/// <para>
/// <b>Declined rather than guessed</b> when the answer cannot be told from the text: a binding macro
/// (<c>.all(x, …)</c> binds a variable that may shadow the field — looked for outside string literals only, so
/// <c>'.all('</c> as data does not decline), a triple-quoted string, or a field named like a CEL reserved word.
/// The caller then names the place instead of rewriting it.
/// </para>
/// <para>
/// <b>A reserved word as the new name is declined too</b>, as defence in depth behind
/// <c>WorkingCopy.RenameField</c>'s own refusal: <c>is_public || owner_id == @user.id</c> rewritten to
/// <c>true || …</c> still compiles, and lets every row through.
/// </para>
/// </remarks>
internal static class CelNames
{
    /* The core lexer's keywords (true, false, null, in, has — CelLexer.KeywordKind) and the CEL spec's
       reserved words. */
    private static readonly HashSet<string> _reserved = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "in", "has", "as", "break", "const", "continue", "else", "for", "function", "if",
        "import", "let", "loop", "package", "namespace", "return", "var", "void", "while",
    };

    private static readonly string[] _binders = [".all(", ".exists(", ".exists_one(", ".map(", ".filter("];

    private static readonly string[] _tripleQuotes = ["'''", "\"\"\""];

    private static readonly string[] _images = ["new", "old"];

    /// <summary>The expression with every reference to <paramref name="from"/> renamed, or <see langword="null"/> when that cannot be done safely.</summary>
    public static string? Rename(string cel, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(cel);
        if (Declines(cel, from, to))
        {
            return null;
        }

        var output = new StringBuilder(cel.Length);
        for (var at = 0; at < cel.Length;)
        {
            at = cel[at] is '\'' or '"' ? CopyString(cel, at, output)
                : IsStart(cel[at]) && !IsPart(Before(cel, at)) ? CopyName(cel, at, from, to, output)
                : Copy(cel, at, output);
        }

        return output.ToString();
    }

    /// <summary>Whether the text contains the name as a whole word — for a place <see cref="Rename"/> declined.</summary>
    public static bool MayName(string cel, string name)
        => Regex.IsMatch(cel, @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Whether the names in this expression cannot be told apart from the text alone.</summary>
    private static bool Declines(string cel, string from, string to)
    {
        if (_reserved.Contains(from) || _reserved.Contains(to)
            || _tripleQuotes.Any(quote => cel.Contains(quote, StringComparison.Ordinal)))
        {
            return true;
        }

        var code = Code(cel);
        return _binders.Any(binder => code.Contains(binder, StringComparison.Ordinal));
    }

    /// <summary>The expression with every string literal blanked, so what a literal says is never read as code.</summary>
    private static string Code(string cel)
    {
        var code = new StringBuilder(cel.Length);
        var discarded = new StringBuilder();
        for (var at = 0; at < cel.Length;)
        {
            if (cel[at] is '\'' or '"')
            {
                at = CopyString(cel, at, discarded);
                code.Append(' ');
            }
            else
            {
                code.Append(cel[at++]);
            }
        }

        return code.ToString();
    }

    private static int CopyString(string cel, int at, StringBuilder output)
    {
        var quote = cel[at];
        var end = at + 1;
        while (end < cel.Length && cel[end] != quote)
        {
            end += cel[end] == '\\' ? 2 : 1;
        }

        end = Math.Min(end + 1, cel.Length);
        output.Append(cel, at, end - at);
        return end;
    }

    private static int CopyName(string cel, int at, string from, string to, StringBuilder output)
    {
        var end = at;
        while (end < cel.Length && IsPart(cel[end]))
        {
            end++;
        }

        var name = cel[at..end];
        output.Append(string.Equals(name, from, StringComparison.Ordinal) && IsColumn(cel, at, end) ? to : name);
        return end;
    }

    private static int Copy(string cel, int at, StringBuilder output)
    {
        output.Append(cel[at]);
        return at + 1;
    }

    /// <summary>A bare name that is not a call or a namespace, or a member of <c>new</c>/<c>old</c>.</summary>
    private static bool IsColumn(string cel, int at, int end)
    {
        /* A name followed by '(' is a call; one followed by '.' is a namespace (math.round) or an image (new., old.), never
           a column: Alvo has no other dotted field path (cel.md deviation 8), so renaming it could only rewrite a call
           (spec §13). */
        if (NextNonSpace(cel, end) is '.' or '(')
        {
            return false;
        }

        var before = PreviousNonSpace(cel, at, out var dot);
        return before switch
        {
            '@' => false,
            '.' => IsImageBefore(cel, dot),
            _ => true,
        };
    }

    private static bool IsImageBefore(string cel, int dot)
    {
        PreviousNonSpace(cel, dot, out var last);
        var start = last + 1;
        while (start > 0 && IsPart(cel[start - 1]))
        {
            start--;
        }

        return _images.Contains(cel[start..(last + 1)]) && PreviousNonSpace(cel, start, out _) is not ('.' or '@');
    }

    private static char PreviousNonSpace(string cel, int at, out int position)
    {
        position = at - 1;
        while (position >= 0 && char.IsWhiteSpace(cel[position]))
        {
            position--;
        }

        return position >= 0 ? cel[position] : '\0';
    }

    private static char NextNonSpace(string cel, int at)
    {
        while (at < cel.Length && char.IsWhiteSpace(cel[at]))
        {
            at++;
        }

        return at < cel.Length ? cel[at] : '\0';
    }

    private static char Before(string cel, int at) => at == 0 ? ' ' : cel[at - 1];

    private static bool IsStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
