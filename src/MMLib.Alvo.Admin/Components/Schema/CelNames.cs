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
/// <b>Relation paths change the spelling, not the rules</b> (<see cref="CelSpelling"/>): in a rule or a lookup check
/// the refs to the field's entity are prefixes beside <c>new</c>/<c>old</c> (<c>work_order_id.assigned_to</c>), another
/// entity's bare names are its own columns, and a ref being renamed is also the head of a path
/// (<c>work_order_id.status</c>) — unless the member after the dot is called, which makes the ref a receiver.
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

    /// <summary>The expression with every reference to <paramref name="from"/> in its own entity renamed, or <see langword="null"/> when that cannot be done safely.</summary>
    public static string? Rename(string cel, string from, string to) => Rename(cel, from, to, CelSpelling.Own);

    /// <summary>The expression with every reference to <paramref name="from"/> spelled as <paramref name="spelling"/> says renamed, or <see langword="null"/> when that cannot be done safely.</summary>
    public static string? Rename(string cel, string from, string to, CelSpelling spelling)
    {
        ArgumentNullException.ThrowIfNull(cel);
        ArgumentNullException.ThrowIfNull(spelling);
        if (Declines(cel, from, to))
        {
            return null;
        }

        var output = new StringBuilder(cel.Length);
        for (var at = 0; at < cel.Length;)
        {
            at = cel[at] is '\'' or '"' ? CopyString(cel, at, output)
                : IsStart(cel[at]) && !IsPart(Before(cel, at)) ? CopyName(cel, at, new(from, to, spelling), output)
                : Copy(cel, at, output);
        }

        return output.ToString();
    }

    /// <summary>Whether the text contains the name as a whole word — for a place <see cref="Rename(string, string, string, CelSpelling)"/> declined.</summary>
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

    private static int CopyName(string cel, int at, Renaming renaming, StringBuilder output)
    {
        var end = NameEnd(cel, at);
        var name = cel[at..end];
        output.Append(string.Equals(name, renaming.From, StringComparison.Ordinal) && IsColumn(cel, at, end, renaming.Spelling) ? renaming.To : name);
        return end;
    }

    private static int NameEnd(string cel, int at)
    {
        while (at < cel.Length && IsPart(cel[at]))
        {
            at++;
        }

        return at;
    }

    private static int Copy(string cel, int at, StringBuilder output)
    {
        output.Append(cel[at]);
        return at + 1;
    }

    /// <summary>A bare name that is not a call or a namespace, a member of one of the spelling's prefixes, or the head of a relation path.</summary>
    private static bool IsColumn(string cel, int at, int end, CelSpelling spelling)
    {
        /* A name followed by '(' is a call; one followed by '.' is a namespace (math.round) or an image (new., old.),
           a column only where the slot reads relation paths and the field is the ref that heads one (spec §13). */
        var before = PreviousNonSpace(cel, at, out var dot);
        return NextNonSpace(cel, end, out var next) switch
        {
            '(' => false,
            '.' => spelling.Head && before is not ('.' or '@') && IsMemberRead(cel, next + 1),
            _ => before switch
            {
                '@' => false,
                '.' => IsPrefixBefore(cel, dot, spelling.Prefixes),
                _ => spelling.Bare,
            },
        };
    }

    /// <summary>Whether a field, not a call, follows the dot: <c>ref.status</c> rather than <c>ref.size()</c>.</summary>
    private static bool IsMemberRead(string cel, int afterDot)
    {
        NextNonSpace(cel, afterDot, out var start);
        var end = NameEnd(cel, start);
        return end > start && NextNonSpace(cel, end, out _) != '(';
    }

    private static bool IsPrefixBefore(string cel, int dot, IReadOnlyCollection<string> prefixes)
    {
        PreviousNonSpace(cel, dot, out var last);
        var start = last + 1;
        while (start > 0 && IsPart(cel[start - 1]))
        {
            start--;
        }

        return prefixes.Contains(cel[start..(last + 1)]) && PreviousNonSpace(cel, start, out _) is not ('.' or '@');
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

    private static char NextNonSpace(string cel, int at, out int position)
    {
        position = at;
        while (position < cel.Length && char.IsWhiteSpace(cel[position]))
        {
            position++;
        }

        return position < cel.Length ? cel[position] : '\0';
    }

    private static char Before(string cel, int at) => at == 0 ? ' ' : cel[at - 1];

    private static bool IsStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private sealed record Renaming(string From, string To, CelSpelling Spelling);
}
