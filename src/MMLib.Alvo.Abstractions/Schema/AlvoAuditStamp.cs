namespace MMLib.Alvo.Schema;

/// <summary>
/// What the framework writes into an <c>audit</c> entity's managed columns, and on which write path —
/// the one authority every <see cref="Data.IAlvoData"/> implementation stamps a row through.
/// </summary>
/// <remarks>
/// <para>
/// The columns exist because an operator turned <c>audit</c> on to get a record they can trust, so they
/// are the framework's to write and a caller's payload may never carry them
/// (<see cref="AlvoManagedColumns.IsCallerWritable"/>). Before this, nothing populated them at all: two
/// of the four are <c>required</c>, so an audited create <em>failed</em> unless the caller supplied the
/// very columns they must not be allowed to author — which is what made the hole tempting rather than
/// theoretical.
/// </para>
/// <para>
/// It lives in the ports, as one function of its inputs, because there are already two shipped
/// implementations of <see cref="Data.IAlvoData"/> and a third arrives with the dynamic driver. A
/// per-implementation copy of "what an audit stamp is" is how a reference implementation comes to record
/// something a real backend does not, and a fixture that cannot reproduce production is how a suite comes
/// to be green about the wrong thing.
/// </para>
/// <para>
/// <b>The instant comes from a <see cref="TimeProvider"/>, never from
/// <see cref="DateTimeOffset.UtcNow"/> inline.</b> An inline clock cannot be asserted on, and what the
/// framework stamps is exactly the kind of behaviour a test has to pin.
/// </para>
/// </remarks>
public static class AlvoAuditStamp
{
    /// <summary>
    /// <paramref name="values"/> with this write's audit stamp applied, or <paramref name="values"/>
    /// itself when <paramref name="entity"/> declares no <c>audit</c>.
    /// </summary>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="values">The caller's payload, already refused if it named a managed column.</param>
    /// <param name="context">The caller the write is performed as — the actor the stamp records.</param>
    /// <param name="time">The clock the stamped instant is read from.</param>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    /// <remarks>
    /// <para>
    /// A <b>create</b> stamps all four columns, not only the two named for it: <c>updated_at</c> is
    /// <c>required</c>, so a row whose first write left it empty violates the column's own <c>NOT NULL</c>,
    /// and "last written" is genuinely the creation instant for a row that has only been created. An
    /// <b>update</b> stamps only <c>updated_at</c>/<c>updated_by</c> — rewriting the creation record on
    /// every write would erase the authorship the audit trail exists to hold.
    /// </para>
    /// <para>
    /// The actor is <see langword="null"/> for a caller with no identity. The all-zero
    /// <see cref="UserId"/> is reserved to mean exactly that (see its own remarks), so recording it as an
    /// author would assert that the anonymous caller wrote the row.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, object?> Applied(
        EntitySchema entity,
        IReadOnlyDictionary<string, object?> values,
        AlvoContext context,
        TimeProvider time,
        bool isUpdate)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);

        if (!entity.Audit)
        {
            return values;
        }

        var stamped = new Dictionary<string, object?>(values, StringComparer.Ordinal)
        {
            [AlvoManagedColumns.UpdatedAt] = time.GetUtcNow(),
            [AlvoManagedColumns.UpdatedBy] = Actor(context),
        };

        if (!isUpdate)
        {
            stamped[AlvoManagedColumns.CreatedAt] = stamped[AlvoManagedColumns.UpdatedAt];
            stamped[AlvoManagedColumns.CreatedBy] = stamped[AlvoManagedColumns.UpdatedBy];
        }

        return stamped;
    }

    private static Guid? Actor(AlvoContext context) =>
        context.User.Value == Guid.Empty ? null : context.User.Value;
}
