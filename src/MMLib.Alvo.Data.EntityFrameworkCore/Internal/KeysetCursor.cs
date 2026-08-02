using System.Buffers.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The opaque keyset cursor this provider issues and accepts: base64url over the anchor row's primary
/// key, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a serialization of the sort tuple. The anchor row's sort-key values are re-read from
/// the database <b>under the same policy predicate</b> as the page itself, so a stale, forged or
/// cross-tenant cursor finds no anchor and yields an empty page rather than telling its holder anything
/// about a row they cannot see. The cost is one extra round trip per page; the benefit is that a cursor
/// carries no data and therefore cannot leak any, and that the encoding stays free to change because
/// only this provider ever reads it.
/// </para>
/// <para>
/// A cursor is caller-supplied text, so every rejection here has to be a <see langword="false"/> rather
/// than an exception — and <see cref="Base64Url.TryDecodeFromChars"/> does not deliver that on its own:
/// despite the <c>Try</c> name it <b>throws</b> <see cref="FormatException"/> on a non-alphabet character
/// and only returns <see langword="false"/> for a destination it cannot fill. Validating the text first is
/// what keeps a forged cursor an empty page instead of an unhandled exception out of a query.
/// </para>
/// </remarks>
internal static class KeysetCursor
{
    private const int RowIdByteCount = 16;

    internal static string Encode(Guid rowId) => Base64Url.EncodeToString(rowId.ToByteArray());

    internal static bool TryDecode(string? cursor, out Guid rowId)
    {
        rowId = default;
        if (string.IsNullOrEmpty(cursor) || !Base64Url.IsValid(cursor))
        {
            return false;
        }

        Span<byte> raw = stackalloc byte[RowIdByteCount];
        if (!Base64Url.TryDecodeFromChars(cursor, raw, out var written) || written != RowIdByteCount)
        {
            return false;
        }

        rowId = new Guid(raw);
        return true;
    }
}
