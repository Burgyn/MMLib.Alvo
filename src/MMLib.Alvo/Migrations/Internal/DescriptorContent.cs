using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Migrations.Internal;

/// <summary>
/// The one notion of "the same descriptor" every apply path shares: two descriptors are the same when their
/// <see cref="AlvoDescriptor.Serialize"/> forms are ordinally equal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical form rather than raw bytes, because raw bytes answer the wrong question.</b>
/// <see cref="AlvoDescriptor"/> guarantees semantic round-trip fidelity and explicitly not byte identity, so
/// reformatting a descriptor — reindenting it, reordering its keys, a different tool writing the same
/// project — changes the bytes and nothing else. A comparison over raw JSON would call that a new descriptor.
/// </para>
/// <para>
/// <b>Extracted rather than duplicated.</b> <see cref="RuntimeSchemaService"/> made this comparison first, to
/// tell a rules-only change from an identical resubmission; <see cref="DescriptorHistoryOrder"/> needs the
/// same one to tell an old pod from a forward deploy. Two private copies would be two definitions of
/// descriptor identity, free to drift apart — and the two paths disagreeing about whether a descriptor is
/// "the same one" is precisely the class of defect the ordering gate exists to catch.
/// </para>
/// <para>
/// <b>Not a hash.</b> A digest would make the comparison smaller and would buy a canonical-form-to-bytes
/// contract that has to survive every future serializer change — a durable format, in effect, since it would
/// be compared against values written by older builds. The comparison here is bounded by one boot's history
/// read, which is not where that trade pays.
/// </para>
/// </remarks>
internal static class DescriptorContent
{
    /// <summary>The canonical form of a parsed descriptor.</summary>
    /// <param name="descriptor">The descriptor to canonicalize.</param>
    internal static string Canonical(AlvoDescriptor descriptor) => AlvoDescriptor.Serialize(descriptor);

    /// <summary>The canonical form of descriptor JSON, whatever shape it was written in.</summary>
    /// <param name="json">The descriptor JSON to canonicalize.</param>
    internal static string Canonical(string json) => Canonical(AlvoDescriptor.Parse(json));

    /// <summary>Whether <paramref name="descriptor"/> is, canonically, the descriptor <paramref name="storedJson"/> holds.</summary>
    /// <param name="descriptor">The descriptor in hand, already parsed.</param>
    /// <param name="storedJson">Descriptor JSON as some store recorded it.</param>
    internal static bool IsSame(AlvoDescriptor descriptor, string storedJson)
        => string.Equals(Canonical(descriptor), Canonical(storedJson), StringComparison.Ordinal);
}
