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
/// <b>Three exception families, and the boundary between them is the contract — not a detail.</b> A layer
/// above this port (PR3's RFC 7807 problem-details layer) has nothing but the exception type to map a status
/// code from, so an implementation must place every refusal in exactly one of these:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Family</term>
///     <description>Means, and what a request layer should render</description>
///   </listheader>
///   <item>
///     <term><see cref="ArgumentException"/> (including its derived types)</term>
///     <description>
///     <b>The query or payload is malformed.</b> A filter past
///     <see cref="AlvoFilter.MaxDepth"/>/<see cref="AlvoFilter.MaxTerms"/>/<see cref="AlvoFilter.MaxInCandidates"/>,
///     a negative <see cref="AlvoQuery.Limit"/>, a paged read sorted by a nullable field, an <c>is</c> operand
///     that is not <see langword="null"/>/<see langword="true"/>/<see langword="false"/>, an <c>in</c> operand
///     that is not a list, a value the field's own type cannot hold, a fractional bound against an integral
///     field, or a <see langword="null"/> where a nested filter belongs. Nothing about the caller's
///     permissions is in question and nothing is being hidden: the shape is wrong. Render 422 with the
///     message's fix suggestion.
///     </description>
///   </item>
///   <item>
///     <term><see cref="AlvoAuthorizationException"/></term>
///     <description>
///     <b>The operation is not permitted.</b> No policy allows it, a filter or sort names a field this caller
///     may not read, a payload names a framework-managed or read-only field, or a candidate post-image fails
///     <c>WITH CHECK</c> or the tenant scope. Render 403.
///     </description>
///   </item>
///   <item>
///     <term><see cref="InvalidOperationException"/></term>
///     <description>
///     <b>An invariant the implementation itself relies on is broken</b> — a schema this port cannot serve, a
///     field the read model does not map, a bound value with no known origin. Never caused by a
///     well-formed request from an authorized caller. Render 500.
///     </description>
///   </item>
/// </list>
/// <para>
/// The two shipped implementations are held to this by
/// <c>AlvoDataAdversarialTests.A_malformed_filter_is_refused_on_the_malformed_query_channel</c>, which exists
/// because they once gave <em>four different answers</em> to four malformed inputs — including
/// <see cref="AlvoAuthorizationException"/> for an ordinary typo like <c>status=is.hello</c>, i.e. a 403 with
/// no fix suggestion, in a framework whose principle 4 is structured errors <em>with</em> fix suggestions.
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
/// <b>The framework-managed <c>id</c>/<c>tenant_id</c> columns are never caller-writable, but not
/// symmetrically.</b> <c>id</c> is rejected in both a <see cref="CreateAsync"/> and an
/// <see cref="UpdateAsync"/> payload — it is assigned once, by the implementation, and never
/// rewritten. <c>tenant_id</c> is different: it is legitimately caller-supplied on
/// <see cref="CreateAsync"/> (a tenant-scoped entity's <c>WITH CHECK</c>/<see cref="PolicyDecision.TenantScope"/>
/// guards the candidate row's post-image there, exactly like every other field the check
/// predicate constrains), but rejected outright on <see cref="UpdateAsync"/> — a row can never move
/// to another tenant once created. Both rejections are checked against the payload alone, before
/// any row lookup runs, so a caller cannot use "was my <c>id</c>/<c>tenant_id</c> write rejected or
/// not" to learn whether a given row id exists; both raise <see cref="AlvoAuthorizationException"/>,
/// never <see cref="AlvoRecordNotFoundException"/>, since the row (if any) was never consulted.
/// </para>
/// <para>
/// <b>A write payload may only name fields the entity's schema declares.</b> A key naming no field at
/// all is refused with <see cref="AlvoAuthorizationException"/> — the same class of refusal every other
/// unwritable-field rejection uses, never an <see cref="ArgumentException"/> — and the message names
/// neither the entity nor the key, since the key is caller-supplied text and a message naming both
/// answers "does this entity have a field called X?" one request at a time. An entity the
/// implementation's own schema does not know refuses the write outright rather than skipping the check:
/// a mismatch between the policy catalog and the implementation's schema must not be the one path on
/// which an unvalidated payload reaches storage.
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
    /// <remarks>
    /// <para>
    /// <b>A filter or sort key may only name a field the caller can actually read.</b> Filtering,
    /// sorting and paging are applied to the stored row while masking is applied to the response, so a
    /// filter over a field in <see cref="PolicyDecision.HiddenFields"/> would leak that field one
    /// comparison per request and a sort over one would leak its ordering across the whole page. An
    /// implementation must reject both — masks fail closed, so the query is refused, never answered
    /// with the offending term quietly dropped.
    /// </para>
    /// <para>
    /// <b>A filter or sort key must also name a field the entity's schema actually declares.</b> This
    /// is the one caller-supplied string an implementation interpolates into <c>WHERE</c>/
    /// <c>ORDER BY</c> as an <em>identifier</em> — SQL has no bind-parameter form of a column name — so
    /// validating it here, against the schema, is what keeps that interpolation safe; an implementation
    /// must not rely on the engine's own unknown-column error, which happens after the statement is
    /// composed. The refusal must be indistinguishable from the hidden-field refusal above and must not
    /// echo the offending name (it is attacker-controlled text): a caller must not be able to tell
    /// "exists but hidden from you" from "does not exist".
    /// </para>
    /// <para>
    /// <b>A filter tree deeper than <see cref="AlvoFilter.MaxDepth"/> is refused, not walked.</b> Every
    /// backend walks a filter recursively, so an implementation must call
    /// <see cref="AlvoFilter.EnsureWithinLimits"/> before doing so — the one malformed-argument
    /// rejection on this port, deliberately an <see cref="ArgumentException"/> rather than an
    /// authorization failure, because it discloses nothing about the schema or the caller's access and a
    /// caller needs to know their query shape was refused rather than their permissions.
    /// </para>
    /// </remarks>
    /// <param name="query">The entity, filter, sort, and paging to apply.</param>
    /// <param name="context">The caller performing the query.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// One page of every visible, matching row, with every <c>hidden</c> field stripped.
    /// <see cref="AlvoPage.NextCursor"/> is an opaque, provider-issued token — only the implementation that
    /// issued it may interpret a later <see cref="AlvoQuery.After"/> carrying it back, and it is
    /// <see langword="null"/> exactly when this page is the last one the query has. <see cref="AlvoPage.TotalCount"/>
    /// is always <see langword="null"/> in F3: no implementation runs a <c>COUNT</c> query, because nothing
    /// here has asked for one yet.
    /// </returns>
    /// <exception cref="AlvoAuthorizationException">
    /// No policy allows <c>list</c> on this entity for <paramref name="context"/>, or
    /// <paramref name="query"/>'s filter or sort names a field this caller may not read or the schema
    /// does not declare.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s filter nests deeper than <see cref="AlvoFilter.MaxDepth"/>, or its paging
    /// window is malformed — see <see cref="AlvoQuery.EnsurePagingWindowIsSane"/>.
    /// </exception>
    Task<AlvoPage> QueryAsync(AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default);

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
    /// No policy allows <c>create</c> on this entity for <paramref name="context"/>,
    /// <paramref name="values"/> supplies <c>id</c> (always rejected — see the type remarks),
    /// the candidate row fails its <c>WITH CHECK</c> predicate, or <paramref name="values"/> writes
    /// a field the policy marks read-only. <c>tenant_id</c> is not rejected here — see the type
    /// remarks — but a value that fails the tenant scope still raises this exception via
    /// <c>WITH CHECK</c>.
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
    /// No policy allows <c>update</c> on this entity for <paramref name="context"/>;
    /// <paramref name="values"/> supplies <c>id</c> or <c>tenant_id</c> (both always rejected on
    /// update — see the type remarks — and checked against the payload before <paramref name="id"/>
    /// is looked up, so this can never be used to probe whether a row exists); the post-image fails
    /// its <c>WITH CHECK</c> predicate; or <paramref name="values"/> writes a field the policy marks
    /// read-only.
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
