using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data;

/// <summary>
/// The single seam every Alvo data operation goes through: policy is enforced <em>inside</em>
/// an implementation of this port, not layered on top of it and not left to the caller. There is
/// no way to read or write a row without going through <see cref="IPolicyEngine"/> first — this
/// is the port the whole security core (context, CEL, tenancy, the rule engine) exists to make
/// enforceable, and PR2's SQLite/PostgreSQL implementations are held to the identical adversarial
/// suite (<c>MMLib.Alvo.Testing.AlvoDataAdversarialTests</c>) this reference implementation is
/// proven against first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every member takes <see cref="AlvoContext"/> explicitly.</b> The obvious alternative —
/// an ambient accessor resolving the current caller from ASP.NET Core's ambient state — silently
/// breaks the moment code runs outside a request: the outbox dispatcher, an after-hook, and an
/// automation action all call into <see cref="IAlvoData"/> with no HTTP request in flight, so an
/// ambient accessor there would resolve to an empty context or a leftover scope from whatever
/// request last used the thread. A wrong or missing tenant on exactly those paths is
/// catastrophic — a post-commit hook silently acting across every tenant's data — so
/// <see cref="AlvoContext"/> is a required parameter everywhere, forcing every call site to state
/// explicitly who it is acting as (frequently <see cref="AlvoContext.System"/>).
/// </para>
/// <para>
/// <b>The failure contract, chosen so nothing leaks the existence of an invisible row.</b> A row
/// that exists but that the caller's policy <c>USING</c> predicate excludes must read exactly
/// like a row that was never there: <see cref="GetAsync"/> returns <see langword="null"/>, and
/// <see cref="UpdateAsync"/>/<see cref="DeleteAsync"/> throw <see cref="AlvoRecordNotFoundException"/>
/// — the same outcome an absent id produces. An operation that is denied outright (no policy
/// configured for it at all, or a candidate write that fails its <c>WITH CHECK</c> predicate)
/// instead throws <see cref="AlvoAuthorizationException"/>, because there the caller is not
/// probing for a specific row's existence; they are attempting something no policy permits at
/// all. Neither exception's message names the entity, the row id, or whether the row exists.
/// </para>
/// <para>
/// <see cref="AlvoAuthorizationException"/>'s and <see cref="AlvoRecordNotFoundException"/>'s
/// message reaches the caller verbatim at this port boundary (an implementation is not required to
/// further generalize <c>IPolicyEngine</c>'s own deny reason). That reason is already designed, at the
/// policy layer, never to name the entity or echo caller-supplied text — except the tenant guard's
/// reason, which deliberately names "tenant" (a narrow, intentional oracle: whether an entity is
/// tenant-scoped at all). A caller building an HTTP layer on this port that wants to withhold even
/// that distinction must map the message to something more generic itself and log the original.
/// </para>
/// <para>
/// <b>The returned key set and CLR types are part of the contract, not an implementation detail.</b>
/// A returned <see cref="AlvoRecord"/> carries every non-hidden field the schema declares for that
/// entity, including framework-managed columns (<c>id</c>, and — on a tenant-scoped entity —
/// <c>tenant_id</c>); masking removes only descriptor-declared <c>hidden</c> fields, never a
/// framework column. Field values use the same CLR types <see cref="AlvoRecord"/>'s own remarks
/// describe the interpreter reading (<see cref="Guid"/> for a <c>uuid</c> field, never a
/// <see cref="string"/> or a byte array; <see cref="DateTimeOffset"/> for a timestamp; <c>decimal</c>
/// for a <c>decimal</c> field), so a caller of this port — and the adversarial suite itself — can
/// assert on a field's value without first normalizing it.
/// </para>
/// </remarks>
public interface IAlvoData
{
    /// <summary>
    /// Lists an entity's rows visible to <paramref name="context"/>: every row that satisfies
    /// both the resolved policy predicate and <paramref name="query"/>'s own
    /// <see cref="AlvoQuery.Filter"/>. The caller's filter can only narrow this result, never
    /// widen it past what policy already allows.
    /// </summary>
    /// <param name="query">The entity, filter, sort, and paging to apply.</param>
    /// <param name="context">The caller performing the query.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Every visible, matching row, with every <c>hidden</c> field stripped.</returns>
    /// <exception cref="AlvoAuthorizationException">No policy allows <c>list</c> on this entity for <paramref name="context"/>.</exception>
    Task<IReadOnlyList<AlvoRecord>> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default);

    /// <summary>Reads a single row by id.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The row id.</param>
    /// <param name="context">The caller performing the read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The row, with every <c>hidden</c> field stripped, or <see langword="null"/> when it does
    /// not exist or the caller's policy excludes it — the two are indistinguishable.
    /// </returns>
    /// <exception cref="AlvoAuthorizationException">No policy allows <c>get</c> on this entity for <paramref name="context"/>.</exception>
    Task<AlvoRecord?> GetAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default);

    /// <summary>Creates a row.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="values">The field values to write; the id is always assigned by the implementation.</param>
    /// <param name="context">The caller performing the create.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The created row, with every <c>hidden</c> field stripped.</returns>
    /// <exception cref="AlvoAuthorizationException">
    /// No policy allows <c>create</c> on this entity for <paramref name="context"/>, the candidate
    /// row fails its <c>WITH CHECK</c> predicate, or <paramref name="values"/> writes a field the
    /// policy marks read-only.
    /// </exception>
    Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default);

    /// <summary>Updates a row by id with a partial set of field values.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The row id.</param>
    /// <param name="values">
    /// The field values to change; a field this dictionary does not mention keeps its stored
    /// value — <c>WITH CHECK</c> is evaluated over the complete post-image (the stored row merged
    /// with these values), never over <paramref name="values"/> alone.
    /// </param>
    /// <param name="context">The caller performing the update.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The updated row, with every <c>hidden</c> field stripped.</returns>
    /// <exception cref="AlvoRecordNotFoundException">The row does not exist, or the caller's policy <c>USING</c> predicate excludes it.</exception>
    /// <exception cref="AlvoAuthorizationException">
    /// No policy allows <c>update</c> on this entity for <paramref name="context"/>, the post-image
    /// fails its <c>WITH CHECK</c> predicate, or <paramref name="values"/> writes a field the
    /// policy marks read-only.
    /// </exception>
    Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default);

    /// <summary>Deletes a row by id.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The row id.</param>
    /// <param name="context">The caller performing the delete.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="AlvoRecordNotFoundException">The row does not exist, or the caller's policy <c>USING</c> predicate excludes it.</exception>
    /// <exception cref="AlvoAuthorizationException">No policy allows <c>delete</c> on this entity for <paramref name="context"/>.</exception>
    Task DeleteAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default);
}
