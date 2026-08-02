using MMLib.Alvo.Schema;
using System.Collections.Frozen;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// <b>The one authority on why a descriptor may not declare a framework-managed column, stated per
/// name.</b> Both passes that refuse the declaration — the mapper's exception and the validator's
/// structured error — read this table and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not folded into <see cref="UnhonouredFeatures"/>.</b> That table is "the schema declares
/// this and this build does not honour it", and every entry leaves when the feature lands. This is a
/// different kind of refusal that never leaves: the framework <em>owns</em> these seven names on the
/// entities whose traits carry them, so a declaration is either redundant or wrong, permanently. Merging
/// them would put a temporary list and a permanent one in one table and make "shrinking this table is what
/// implementing a feature means" false of half its rows.
/// </para>
/// <para>
/// <b>Why declaring the name is refused rather than merged, and it is not a tidiness rule.</b> The mapper
/// used to let a declaration win — it injected a managed column only when the entity did not already
/// declare that name — and two reachable defects came out of that one branch:
/// </para>
/// <list type="bullet">
///   <item>
///   An audited entity declaring <c>updated_at</c> as <c>{"type":"string"}</c> passed apply, and then
///   <b>every create answered 422 with an internal <c>(Parameter 'value')</c> in the body</b> — the audit
///   stamp writes a <see cref="DateTimeOffset"/> into a column the schema says is text. Measured; the
///   suite's own response screen catches the leak, and no descriptor reached it.
///   </item>
///   <item>
///   The same entity declaring <c>updated_at</c> with <c>hidden</c> passed apply and silently switched
///   <b>optimistic concurrency off</b>: the mask drops the key from every returned record, so no
///   <c>ETag</c> is minted and a caller has nothing to send as <c>If-Match</c>. Nothing raised anywhere.
///   </item>
/// </list>
/// <para>
/// A narrower rule was tried first — refuse only <c>hidden</c> on a managed column — and it closed the
/// second defect while leaving the first, which is worse. Refusing the declaration closes both, and it
/// closes them at the one place they share.
/// </para>
/// <para>
/// <b>Trait-scoped, never a flat name list.</b> An entity that does not declare <c>audit</c> may
/// legitimately declare an ordinary field called <c>created_at</c>, and refusing that would refuse a field
/// the framework does not manage — the reason
/// <see cref="AlvoManagedColumns.For(TenancyMode?, bool, bool)"/> answers membership from traits in the
/// first place. The set consulted here is exactly the set the mapper injects, so the two cannot disagree
/// about which names are owned.
/// </para>
/// <para>
/// <b>One arm per name and no catch-all.</b> The previous version of this text was a three-arm
/// <c>switch</c> with a <c>_</c> default, and the default drifted immediately: it told a
/// <c>softDelete</c>-only entity that <c>deleted_at</c> was "part of the audit trail this entity asked for
/// by declaring 'audit'", false in both halves. A catch-all cannot be wrong about a column it was written
/// for and cannot be right about one it was not. C# will not make an unlisted name a <em>compile</em>
/// error — a <c>switch</c> expression without a discard throws at run time instead, which is a worse
/// failure than a wrong sentence — so the tie is a fact:
/// <c>ManagedColumnNamesTests.Every_managed_column_has_its_own_reason</c> compares these keys against
/// <see cref="AlvoManagedColumns"/>' full set in both directions, and <see cref="Refusing"/> throws rather
/// than inventing prose for a name it does not know.
/// </para>
/// </remarks>
internal static class ManagedColumnNames
{
    /// <summary>
    /// The names the framework injects for an entity with these traits — the set a declaration is refused
    /// against.
    /// </summary>
    /// <param name="tenancy">The entity's resolved tenancy mode, or <see langword="null"/> when the project declares none.</param>
    /// <param name="audit">Whether the entity declares <c>audit</c>.</param>
    /// <param name="softDelete">Whether the entity declares <c>softDelete</c>.</param>
    /// <remarks>
    /// A thin pass-through to <see cref="AlvoManagedColumns"/> on purpose, so this type owns the <em>prose</em>
    /// and the ports keep owning the <em>membership</em>. A second implementation of the trait rule here is
    /// exactly the drift <see cref="AlvoManagedColumns"/>' own remarks describe paying for four times.
    /// </remarks>
    internal static IReadOnlySet<string> InjectedFor(TenancyMode? tenancy, bool audit, bool softDelete) =>
        AlvoManagedColumns.For(tenancy, audit, softDelete);

    /// <summary>
    /// Why <paramref name="column"/> may not be declared, and what to do instead.
    /// </summary>
    /// <param name="column">A framework-managed column name.</param>
    /// <returns>The consequence of letting a declaration win, and the fix.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="column"/> is managed but has no entry here — a managed column was added to
    /// <see cref="AlvoManagedColumns"/> without a reason. It fails loudly rather than falling back to a
    /// catch-all sentence, because a confident wrong explanation is the defect this table was rebuilt to
    /// remove.
    /// </exception>
    internal static (string Consequence, string Fix) Refusing(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return _reasons.TryGetValue(column, out var reason)
            ? reason
            : throw new InvalidOperationException(
                $"'{column}' is a framework-managed column with no recorded reason it cannot be declared. Add "
                + $"an entry for it to {nameof(ManagedColumnNames)}; there is deliberately no catch-all, "
                + "because a generic sentence about the wrong column is worse than no sentence.");
    }

    /// <summary>Every name this table explains, for the fact that ties it to <see cref="AlvoManagedColumns"/>.</summary>
    internal static IReadOnlyCollection<string> Explained => _reasons.Keys;

    /// <summary>
    /// The message every entry ends with. One sentence, once — it is the same instruction for all seven, and
    /// the per-name half is what precedes it.
    /// </summary>
    private const string DeclareYourOwn =
        "If you meant a column of your own, declare it under a different name: the framework owns this one on "
        + "an entity whose traits carry it, and a declaration cannot narrow or retype it.";

    /// <summary>
    /// The one place a caller-visible narrowing was lost by this rule, named where an author will hit it.
    /// </summary>
    /// <remarks>
    /// An earlier, narrower rule permitted <c>readOnly</c> on <c>tenant_id</c>, since that is the one managed
    /// column a caller may write (on a create only). The general rule forbids the declaration and therefore the
    /// flag, and the replacement is a policy rule rather than a field flag — the synthesized tenant scope's
    /// <c>WITH CHECK</c> is already evaluated over the candidate row, so a <c>create</c> rule is where "which
    /// tenant may this row be placed in" belongs and is the only place that can answer it per caller.
    /// </remarks>
    private const string TenantNarrowing =
        "To restrict which tenant a row may be created in, write it as a 'create' rule rather than a field "
        + "flag: the synthesized tenant scope's WITH CHECK is already evaluated over the candidate row, so a "
        + "rule can answer it per caller and a flag cannot.";

    private static readonly FrozenDictionary<string, (string Consequence, string Fix)> _reasons =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [AlvoManagedColumns.Id] = (
                "'id' is the row key the store assigns. A declaration replaces it, so a different type or "
                + "nullability makes the key unusable — and because every response and every following request "
                + "identifies the row by it, a create that cannot read back a 'id' fails with no caller error to "
                + "report.",
                $"Remove 'id' from the entity's fields; the store assigns it. {DeclareYourOwn}"),

            [AlvoManagedColumns.TenantId] = (
                "'tenant_id' is the discriminator the synthesized tenant scope compares, and no other response "
                + "reports which tenant a row belongs to. A declaration that retypes it breaks the comparison "
                + "every scoped read and write is filtered by.",
                $"Remove 'tenant_id' from the entity's fields; 'tenancy' puts it there. {TenantNarrowing} "
                + DeclareYourOwn),

            [AlvoManagedColumns.CreatedAt] = (
                "'created_at' is a required instant the framework stamps on every create. A declaration that "
                + "retypes it — to 'string', say — is accepted at apply and then fails every single create, "
                + "because the stamp writes a timestamp into a column the schema says is something else.",
                "Remove 'created_at' from the entity's fields; 'audit: true' puts it there, and dropping "
                + $"'audit' removes it. {DeclareYourOwn}"),

            [AlvoManagedColumns.CreatedBy] = (
                "'created_by' records which caller created the row, and it is half of the audit trail this "
                + "entity asked for by declaring 'audit'. A declaration retypes or masks a column the framework "
                + "writes on every create regardless.",
                "Remove 'created_by' from the entity's fields; 'audit: true' puts it there, and dropping "
                + $"'audit' removes it. {DeclareYourOwn}"),

            [AlvoManagedColumns.UpdatedAt] = (
                "'updated_at' is the column that versions a row, so a declaration costs more than the others. "
                + "Retyped, it fails every write, because the audit stamp writes a timestamp into a column the "
                + "schema says is something else. Masked with 'hidden', it leaves the API with no 'ETag' to hand "
                + "out and the caller with no 'If-Match' to send — optimistic concurrency off for this entity, "
                + "silently, with concurrent writers overwriting each other.",
                "Remove 'updated_at' from the entity's fields; 'audit: true' puts it there, and dropping "
                + "'audit' removes it — along with the row versioning that 'ETag' and 'If-Match' need. "
                + DeclareYourOwn),

            [AlvoManagedColumns.UpdatedBy] = (
                "'updated_by' records which caller last wrote the row, and it is half of the audit trail this "
                + "entity asked for by declaring 'audit'. A declaration retypes or masks a column the framework "
                + "writes on every write regardless.",
                "Remove 'updated_by' from the entity's fields; 'audit: true' puts it there, and dropping "
                + $"'audit' removes it. {DeclareYourOwn}"),

            [AlvoManagedColumns.DeletedAt] = (
                "'deleted_at' is the marker a soft delete sets and every read excludes on, so it is the whole of "
                + "whether a deleted row is recoverable. A declaration hands that to the caller's own schema.",
                "Remove 'deleted_at' from the entity's fields; 'softDelete' is what puts it there. "
                + DeclareYourOwn),
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
