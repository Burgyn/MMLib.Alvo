using MMLib.Alvo.Schema;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one name a partial index has — a pure function of its whole definition, computed and never parsed (D10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> A prefix, the entity's name cut to its first 46 characters, <c>_</c>, and the first 8 lower-case hex digits
/// of SHA-256 over the canonical definition: <c>3 + 46 + 1 + 8 = 58</c>, inside PostgreSQL's 63-byte identifier limit with
/// room for a two-character suffix a driver derives from it.
/// </para>
/// <para>
/// <b>A changed definition is a new name.</b> Editing <c>where</c>, <c>unique</c> or <c>fields</c> changes the hash, so the
/// plan is a drop of the old name and a create of the new one — EF's differ sees two different indexes, and no
/// in-place alteration of an index predicate is ever needed.
/// </para>
/// </remarks>
internal static class IndexNames
{
    private const int MaxReadable = 46;
    private const int HashDigits = 8;

    /// <summary>The name of <paramref name="entity"/>'s partial index <paramref name="index"/>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="index">An index declaring <see cref="IndexSchema.Where"/>.</param>
    internal static string Partial(EntitySchema entity, IndexSchema index) =>
        Named("ix_", entity.Name, $"{entity.Name}|{string.Join(',', index.Fields)}|{index.Unique}|{index.Where}");

    private static string Named(string prefix, string entity, string canonical) =>
        $"{prefix}{entity[..Math.Min(entity.Length, MaxReadable)]}_{Hash(canonical)}";

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..HashDigits];
}
