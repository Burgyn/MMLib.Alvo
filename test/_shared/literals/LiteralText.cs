using CsCheck;
using System.Text;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The text a computed field's constant can be, drawn from the <b>whole</b> code space, and the test's own oracle for
/// which of it a SQL literal must decline — shared by every literal property so they sample the same space.
/// </summary>
/// <remarks>
/// <para>
/// The space: the quote and the backslash (the two characters a literal escapes), the prefixes, dollar quotes,
/// comment openers and terminators SQL gives meaning to, every UTF-16 code unit — C0, DEL and C1 controls, lone
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
        Gen.OneOfConst("'", "''", "\\", "\\'", "E'", "N'", "$$", "--", "/*", ";", " ", "ž", "中"),
        Gen.Char.Select(character => character.ToString()),
        Gen.Char['\u0000', ' '].Select(character => character.ToString()),
        Gen.Char['\uD800', '\uDFFF'].Select(character => character.ToString()),
        Gen.Char['\uE000', '\uFFFF'].Select(character => character.ToString()),
        Gen.Int[0x10000, 0x10FFFF].Select(char.ConvertFromUtf32));

    /// <summary>Strings of up to <paramref name="maxPieces"/> pieces from the whole space.</summary>
    public static Gen<string> Any(int maxPieces = 24) =>
        _piece.Array[0, maxPieces].Select(pieces => string.Concat(pieces));

    /// <summary>Whether a literal must decline <paramref name="value"/>: a control character or an unpaired surrogate.</summary>
    public static bool MustBeDeclined(string value)
    {
        foreach (var rune in EnumerateOrFail(value))
        {
            if (rune is null || Rune.IsControl(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

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
