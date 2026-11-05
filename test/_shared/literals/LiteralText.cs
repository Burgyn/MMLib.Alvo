using CsCheck;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The text a computed field's constant can be, drawn from the <b>whole</b> code space, and the test's own oracle for
/// which of it a SQL literal must decline — shared by every literal property so they sample the same space.
/// </summary>
/// <remarks>
/// <para>
/// The space: the quote and the backslash (the two characters a literal escapes), the prefixes, dollar quotes,
/// comment openers and terminators SQL gives meaning to, the Unicode line and paragraph separators (drawn on purpose:
/// two code units out of 65 536 would almost never be sampled), every UTF-16 code unit — C0, DEL and C1 controls, lone
/// surrogates and U+E000–U+FFFF included — and astral code points as proper surrogate pairs.
/// </para>
/// <para>
/// The oracle is written here independently of the product's predicate on purpose: a property that asked the product
/// which inputs to expect declined would pass whatever the product declined.
/// </para>
/// </remarks>
internal static class LiteralText
{
    private static readonly Gen<string> _piece = Gen.OneOf(
        Gen.OneOfConst("'", "''", "\\", "\\'", "E'", "N'", "$$", "--", "/*", ";", " ", "ž", "中", "\u2028", "\u2029"),
        Gen.Char.Select(character => character.ToString()),
        Gen.Char['\u0000', ' '].Select(character => character.ToString()),
        Gen.Char['\uD800', '\uDFFF'].Select(character => character.ToString()),
        Gen.Char['\uE000', '\uFFFF'].Select(character => character.ToString()),
        Gen.Int[0x10000, 0x10FFFF].Select(char.ConvertFromUtf32));

    private static readonly Gen<string> _sqlSyntax = Gen.OneOfConst(
        "'", "''", "\\", "\\'", "\\\\", "E'", "e'", "N'", "U&'", "$", "$$", "$x$", "--", "/*", "*/", ";", "(", ")", "\"", " ",
        "DROP TABLE sentinel", "'); DROP TABLE sentinel; --");

    private static readonly Gen<string> _acceptedPiece = Gen.Frequency(
        (6, _sqlSyntax),
        (2, Gen.Char.Where(character => !MustBeDeclined(character.ToString())).Select(character => character.ToString())),
        (1, Gen.OneOfConst("ž", "中", "é")),
        (1, Gen.Int[0x10000, 0x10FFFF].Select(char.ConvertFromUtf32)));

    private static readonly Gen<string> _declinedPiece = Gen.OneOf(
        Gen.Const("\0"),
        Gen.Char['\u0001', '\u001F'].Select(character => character.ToString()),
        Gen.Char['\u007F', '\u009F'].Select(character => character.ToString()),
        Gen.OneOfConst("\u2028", "\u2029"),
        Gen.Char['\uD800', '\uDBFF'].Select(character => character.ToString()),
        Gen.Char['\uDC00', '\uDFFF'].Select(character => character.ToString()));

    /// <summary>Strings of up to <paramref name="maxPieces"/> pieces from the whole space.</summary>
    public static Gen<string> Any(int maxPieces = 24) =>
        _piece.Array[0, maxPieces].Select(pieces => string.Concat(pieces));

    /// <summary>
    /// Text aimed at a SQL literal: two draws in three only from pieces a literal must carry — weighted toward the quote,
    /// the backslash, <c>$</c>, the comment openers and a payload naming the sentinel table — and one in three the same
    /// with exactly one piece a literal must decline (NUL, a C0 or C1 control, U+2028/U+2029, a lone surrogate) inserted at
    /// a drawn position.
    /// </summary>
    /// <remarks>
    /// Built for a property that judges both branches, so nothing is filtered away: a filter over <see cref="Any"/> keeps a
    /// draw of <c>n</c> pieces with probability about <c>0.65^n</c> and shrinks the accepted samples toward the empty string.
    /// </remarks>
    public static Gen<string> Hostile(int maxPieces = 24) =>
        Gen.Frequency((2, Accepted(maxPieces)), (1, DeclinedOnce(maxPieces)));

    private static Gen<string> Accepted(int maxPieces) =>
        _acceptedPiece.Array[0, maxPieces].Select(pieces => string.Concat(pieces));

    private static Gen<string> DeclinedOnce(int maxPieces) =>
        Gen.Select(_acceptedPiece.Array[0, maxPieces - 1], _declinedPiece, Gen.Int[0, maxPieces - 1], (pieces, declined, at) =>
            string.Concat(pieces.Take(at % (pieces.Length + 1)).Append(declined).Concat(pieces.Skip(at % (pieces.Length + 1)))));

    /// <summary>
    /// Whether a literal must decline <paramref name="value"/>: a control character, a line or paragraph separator
    /// (Unicode categories Zl and Zp — asked by category here, where the product names the two code points), or an
    /// unpaired surrogate.
    /// </summary>
    public static bool MustBeDeclined(string value)
    {
        foreach (var rune in EnumerateOrFail(value))
        {
            if (rune is null || Rune.IsControl(rune.Value) || IsLineOrParagraphSeparator(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLineOrParagraphSeparator(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static IEnumerable<Rune?> EnumerateOrFail(string value)
    {
        for (var index = 0; index < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                yield return null;
                yield break;
            }

            yield return rune;
            index += consumed;
        }
    }
}
