namespace MMLib.Alvo.Schema;

/// <summary>
/// The one authority for which columns the framework owns: which names they are, which of them an
/// entity with a given set of traits carries, and which of them a caller may ever supply.
/// </summary>
/// <remarks>
/// <para>
/// It lives here, in the ports, because two very different pieces of code have to agree about it and
/// neither can see the other: the descriptor mapper that <em>injects</em> these columns is
/// <see langword="internal"/> to the core, and the write guard that <em>refuses</em> them is
/// <see langword="internal"/> to a driver package. Before this type they each carried their own list,
/// the mapper's grew to six columns and the guard's stayed at two, and the four it did not know about
/// were caller-writable — audit-trail forgery through a port whose whole job is that a caller cannot
/// write what policy does not allow. Keeping one list in one place is what makes that unrepresentable,
/// and it is the fourth time this codebase has paid for the same defect.
/// </para>
/// <para>
/// The membership question is answered from an entity's <em>traits</em> (tenancy, audit, soft delete)
/// rather than from a flat name list, because a name alone is not enough: an entity that does not
/// declare <c>audit</c> may legitimately declare an ordinary field called <c>created_at</c>, and
/// refusing a write to that would refuse a field the framework does not manage. The traits are exactly
/// what the mapper injects from, so asking the same question of the same inputs is what keeps the two
/// sides in step.
/// </para>
/// <para>
/// Every name is a get-only property rather than a <see langword="const"/>, for the reason
/// <see cref="Data.AlvoFilter.MaxDepth"/> gives: a public <see langword="const"/> is inlined at each
/// consumer's compile time, so a driver compiled against one spelling and a framework enforcing another
/// would disagree silently.
/// </para>
/// </remarks>
public static class AlvoManagedColumns
{
    /// <summary>The row key, assigned once by the implementation and never rewritten.</summary>
    public static string Id => "id";

    /// <summary>The tenant discriminator on a <see cref="TenancyMode.Scoped"/> entity.</summary>
    public static string TenantId => "tenant_id";

    /// <summary>When the row was created, on an <c>audit</c> entity.</summary>
    public static string CreatedAt => "created_at";

    /// <summary>Who created the row, on an <c>audit</c> entity.</summary>
    public static string CreatedBy => "created_by";

    /// <summary>When the row was last written, on an <c>audit</c> entity.</summary>
    public static string UpdatedAt => "updated_at";

    /// <summary>Who last wrote the row, on an <c>audit</c> entity.</summary>
    public static string UpdatedBy => "updated_by";

    /// <summary>When the row was soft-deleted, on a <c>softDelete</c> entity.</summary>
    public static string DeletedAt => "deleted_at";

    /// <summary>
    /// The four columns an <c>audit</c> entity carries, in the order the schema mapper injects them.
    /// </summary>
    public static IReadOnlyList<string> Audit { get; } = [CreatedAt, CreatedBy, UpdatedAt, UpdatedBy];

    /// <summary>
    /// Every column the framework manages for an entity with these traits.
    /// </summary>
    /// <param name="tenancy">The entity's tenancy mode, or <see langword="null"/> when the project declares none.</param>
    /// <param name="audit">Whether the entity declares <c>audit</c>.</param>
    /// <param name="softDelete">Whether the entity declares <c>softDelete</c>.</param>
    public static IReadOnlySet<string> For(TenancyMode? tenancy, bool audit, bool softDelete)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal) { Id };
        if (tenancy == TenancyMode.Scoped)
        {
            columns.Add(TenantId);
        }

        if (audit)
        {
            columns.UnionWith(Audit);
        }

        if (softDelete)
        {
            columns.Add(DeletedAt);
        }

        return columns;
    }

    /// <summary>Every column the framework manages for <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity, as the applied schema declares it.</param>
    public static IReadOnlySet<string> For(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return For(entity.Tenancy, entity.Audit, entity.SoftDelete);
    }

    /// <summary>
    /// Every name the framework owns, on any entity — the union of what <see cref="For(EntitySchema)"/>
    /// can return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the same question as <see cref="For(EntitySchema)"/>, and the difference matters.</b> That
    /// one answers "which columns does <em>this</em> entity have", which is what a payload guard or a
    /// projection needs. This answers "which names are the framework's to give", which is what a layer
    /// needs when it is about to <em>mint</em> a name rather than resolve one — a projection alias being
    /// the case that made this necessary. A global, non-audited entity has no <c>tenant_id</c> and no
    /// <c>created_at</c>, but a response key called either of those still reads as a framework column to
    /// whoever receives it, and no descriptor is allowed to declare one.
    /// </para>
    /// <para>
    /// Derived from <see cref="For(TenancyMode?, bool, bool)"/> rather than written out, so it cannot drift
    /// from it — which is the whole reason this class exists. The cost is a static-initialisation order
    /// dependency: <c>For</c> reads <see cref="Audit"/>, so this initializer must stay <b>below</b> it.
    /// Moving it above would leave <see cref="Audit"/> null and fail at type initialisation.
    /// </para>
    /// <para>
    /// <b><see langword="internal"/> where the rest of this class is public, and the asymmetry is the
    /// point.</b> <see cref="For(EntitySchema)"/> and <see cref="VersionColumn"/> are public because a
    /// provider needs them — an out-of-tree driver must know which columns it may not let a caller write.
    /// Nothing outside this framework mints a name: the one caller is the Data API's own query parser, which
    /// the core reaches through <c>InternalsVisibleTo</c>. Publishing it would have added a member to the
    /// package's surface that no consumer of the package has a use for, and every public member is a
    /// promise that has to be kept.
    /// </para>
    /// </remarks>
    internal static IReadOnlySet<string> All { get; } =
        For(TenancyMode.Scoped, audit: true, softDelete: true);

    /// <summary>
    /// The column whose value versions a row for optimistic concurrency, or <see langword="null"/>
    /// when the entity has none. Only an audited entity has one: <c>updated_at</c> exists because
    /// <c>audit: true</c> asked for it, so a non-audited entity cannot answer "has this row changed"
    /// at all — and a request layer must refuse an <c>If-Match</c> against it rather than pretend.
    /// </summary>
    /// <param name="entity">The entity, as the applied schema declares it.</param>
    /// <remarks>
    /// <para>
    /// Answered from the entity's <em>traits</em> like every other question here, and for the same reason:
    /// an entity that does not declare <c>audit</c> may legitimately declare an ordinary field called
    /// <c>updated_at</c>, and versioning a row by a column the framework does not write would compare a
    /// value the caller themselves can change — a precondition anyone can satisfy is not a precondition.
    /// </para>
    /// <para>
    /// One member rather than a <c>VersionColumn</c>/<c>HasVersion</c> pair: <see langword="null"/> already
    /// answers "has none", and a second way to ask one question is a second thing to keep in step. The
    /// refusal itself lives at <see cref="Data.AlvoPrecondition.EnsureSupported"/>, so both shipped
    /// implementations word it identically.
    /// </para>
    /// </remarks>
    public static string? VersionColumn(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.Audit ? UpdatedAt : null;
    }

    /// <summary>
    /// Whether a caller's own write payload may carry <paramref name="column"/>.
    /// </summary>
    /// <param name="column">A column name, managed or not.</param>
    /// <param name="isUpdate">Whether the write is an update rather than a create.</param>
    /// <remarks>
    /// <c>tenant_id</c> on a create is the one exception, and it is deliberate: a create legitimately
    /// places a row in a tenant, and the synthesized tenant scope evaluated over the candidate row is
    /// what decides whether that tenant is allowed. Every other managed column is the framework's to
    /// write on every path — an audit trail a caller can author is not an audit trail.
    /// </remarks>
    public static bool IsCallerWritable(string column, bool isUpdate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return !isUpdate && string.Equals(column, TenantId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason a caller may not write <paramref name="column"/>, as the refusal message states it.
    /// </summary>
    /// <param name="column">The managed column the payload named.</param>
    /// <param name="isUpdate">Whether the write is an update rather than a create.</param>
    /// <remarks>
    /// The text lives here so every <c>IAlvoData</c> implementation refuses one column with one wording.
    /// Two implementations of one port that word a refusal differently give the port two contracts, and
    /// the inherited suite asserts on the message.
    /// </remarks>
    public static string RefusalReason(string column, bool isUpdate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (string.Equals(column, Id, StringComparison.Ordinal))
        {
            return isUpdate
                ? "is assigned once at creation and can never be rewritten"
                : "is assigned by the store and cannot be supplied on create";
        }

        return string.Equals(column, TenantId, StringComparison.Ordinal)
            ? "is fixed at creation and a row can never move to another tenant"
            : "is managed by the framework and cannot be written by a caller";
    }
}
