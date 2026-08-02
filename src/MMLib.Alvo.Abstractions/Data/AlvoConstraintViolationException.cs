namespace MMLib.Alvo.Data;

/// <summary>
/// Thrown when a well-formed, authorized request collides with <em>stored state</em> the database itself
/// guards: a value another record already holds on a <c>unique</c> field, or a delete a
/// <c>onDelete: "restrict"</c> reference refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own family, and specifically not <see cref="InvalidOperationException"/>.</b> Before this type
/// existed the provider's own exception reached the host and was rendered as
/// <c>alvo.dev/errors/internal</c> — "an invariant Alvo itself relies on is broken". Neither of these is
/// that. Both are the caller's request conflicting with data that was already there, which is the
/// definition of <c>409</c>, and the misclassification had three costs worth stating because each is a
/// separate defect: an agent could not repair the request (no pointer, no field, no fix suggestion, in a
/// framework whose principle 4 is structured errors <em>with</em> one); a <c>500</c> invites a retry that
/// can never succeed; and the operator was paged, with a stack trace, for an ordinary caller mistake.
/// </para>
/// <para>
/// <b>Not <see cref="ArgumentException"/> either.</b> The payload is well-formed and every declared facet
/// the framework can check itself — <c>required</c>, <c>maxLength</c>, <c>enum</c>, <c>format</c>,
/// <c>precision</c> — already passed. What is wrong is its relationship to rows the caller may not even be
/// able to see, which nothing about the payload alone could have revealed. That is
/// <see cref="AlvoIdempotencyConflictException"/>'s reasoning exactly, and it is why both render 409.
/// </para>
/// <para>
/// <b>What <see cref="Fields"/> may and may not contain.</b> Field <em>names</em>, from the entity's own
/// schema — never a value, never a constraint or index name, never the name of another entity. A name is
/// something the caller already sent and the published document already declares, so it discloses nothing;
/// a value would put attacker-controlled bytes into every log that records the response, and an engine's
/// constraint name is an implementation detail of whichever migration created it. Framework-managed columns
/// are excluded, because a caller cannot change one — a collision confined to them is a broken invariant
/// rather than a conflict, and an implementation must let that keep propagating as one.
/// </para>
/// <para>
/// <b><see cref="AlvoConstraintKind.Referenced"/> names nothing at all, and that is deliberate.</b> The
/// referencing entity is knowable from the published schema, but <em>which</em> of the entities that may
/// reference this row actually holds one is a fact about data the caller may have no read access to.
/// Answering "some record still references this one" is already the minimum a <c>restrict</c> refusal must
/// disclose to be a refusal; narrowing it to an entity would disclose more than the constraint itself does.
/// </para>
/// <para>
/// <b>It is not an oracle across tenants — but only because the index is scoped.</b> On a
/// <c>tenancy: "scoped"</c> entity a <c>unique</c> constraint spans <c>(tenant_id, …)</c> (#137), so a
/// caller only ever collides with a row in their own tenant. Mapping the refusal to this exception does
/// <b>not</b> on its own close that leak: a <c>409</c>-versus-<c>201</c> answer is exactly the same one-bit
/// signal a <c>500</c>-versus-<c>201</c> answer was. The index is what removes the signal; this type only
/// makes the remaining, in-tenant refusal repairable.
/// </para>
/// </remarks>
public sealed class AlvoConstraintViolationException : Exception
{
    /// <summary>The wording of a unique collision, free of the value, the field and the constraint's name.</summary>
    private const string UniqueMessage =
        "Another record already holds a value this request supplies on a field declared unique. Send a value "
        + "that is not already taken, or change the record that holds it.";

    /// <summary>The wording of a restrict refusal, naming neither the referencing entity nor how many rows.</summary>
    private const string ReferencedMessage =
        "Other records still reference this record, so it cannot be removed. Delete those records, or point "
        + "them at something else, and retry.";

    /// <summary>Initializes a new instance of the <see cref="AlvoConstraintViolationException"/> class.</summary>
    /// <param name="kind">Which constraint the request collided with.</param>
    /// <param name="fields">
    /// The entity's own field names the conflict concerns, or empty when the engine names none — see the
    /// type remarks for what may appear here.
    /// </param>
    /// <param name="innerException">
    /// The provider exception this was translated from, kept so a host's logging still has the engine's own
    /// diagnostics even though none of it reaches the caller.
    /// </param>
    public AlvoConstraintViolationException(
        AlvoConstraintKind kind, IReadOnlyList<string> fields, Exception? innerException = null)
        : base(MessageFor(kind), innerException)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Kind = kind;
        Fields = [.. fields];
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoConstraintViolationException"/> class.</summary>
    /// <remarks>
    /// The parameterless and message-carrying constructors exist for the CA1032 exception-shape rule and for
    /// a caller that has no field list; both answer <see cref="AlvoConstraintKind.Unique"/>, which is the
    /// only kind a request can reach on a write path that supplies values.
    /// </remarks>
    public AlvoConstraintViolationException()
        : this(AlvoConstraintKind.Unique, [])
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoConstraintViolationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public AlvoConstraintViolationException(string message)
        : base(message)
    {
        Fields = [];
    }

    /// <summary>Initializes a new instance of the <see cref="AlvoConstraintViolationException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AlvoConstraintViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Fields = [];
    }

    /// <summary>Which constraint the request collided with.</summary>
    public AlvoConstraintKind Kind { get; }

    /// <summary>
    /// The entity's own field names the conflict concerns — one for an ordinary <c>unique</c> field, several
    /// for a composite unique index, and none when the engine reports no columns (SQLite says only
    /// <c>FOREIGN KEY constraint failed</c>) or when the kind is <see cref="AlvoConstraintKind.Referenced"/>.
    /// </summary>
    public IReadOnlyList<string> Fields { get; }

    private static string MessageFor(AlvoConstraintKind kind) =>
        kind == AlvoConstraintKind.Referenced ? ReferencedMessage : UniqueMessage;
}
