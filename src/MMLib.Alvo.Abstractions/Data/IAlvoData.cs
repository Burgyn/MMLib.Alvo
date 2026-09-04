using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

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
/// <b>Six exception families, and the boundary between them is the contract — not a detail.</b> A layer
/// above this port (PR3's RFC 7807 problem-details layer) has nothing but the exception type to map a status
/// code from, so an implementation must place every refusal in exactly one of these:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Family</term>
///     <description>Means, and what a request layer should render</description>
///   </listheader>
///   <item>
///     <term><see cref="ArgumentException"/> and its derived types, <b>except <see cref="ArgumentNullException"/></b></term>
///     <description>
///     <b>The query or payload is malformed.</b> A filter past
///     <see cref="AlvoFilter.MaxDepth"/>/<see cref="AlvoFilter.MaxTerms"/>/<see cref="AlvoFilter.MaxInCandidates"/>,
///     a negative <see cref="AlvoQuery.Limit"/>, a paged read sorted by a nullable field, an <c>is</c> operand
///     that is not <see langword="null"/>/<see langword="true"/>/<see langword="false"/>, an <c>in</c> operand
///     that is not a list, a value the field's own type cannot hold, a fractional bound against an integral
///     field, or a <see langword="null"/> where a nested filter belongs. Nothing about the caller's
///     permissions is in question and nothing is being hidden: the shape is wrong. Render 422 with the
///     message's fix suggestion.
///     <para>
///     <b><see cref="ArgumentNullException"/> is excluded and belongs to the last family below</b>, even
///     though it derives from this one. <em>No request can express a null argument.</em> A
///     <see langword="null"/> reaching a member of this port means its caller — the HTTP layer, a hook, an
///     automation action — passed one where this contract forbids it, which is a broken invariant of the
///     code rather than a malformed request. Rendered as a 422 it tells a caller to fix a request that was
///     fine, and it swallows the stack trace a host's logging exists to record. So every
///     <c>ArgumentNullException.ThrowIfNull</c> guarding this port's own parameters raises an
///     implementation defect, never a caller error. PR3's HTTP layer excludes it from the malformed-query
///     arm for exactly this reason; the exclusion is stated <em>here</em> because a provider author reads
///     this table and not that layer.
///     </para>
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
///     <term><see cref="AlvoPreconditionFailedException"/></term>
///     <description>
///     <b>The write carried a version the stored row does not have.</b> The row has been written since the
///     caller read it, or the entity keeps no version of a row at all (no <c>audit</c>, so no
///     <see cref="AlvoManagedColumns.VersionColumn"/>) and cannot answer the question — refused rather than
///     ignored, because a silently ignored precondition is a lost update the caller believes it prevented.
///     Neither the request nor the caller's permissions is at fault, so neither of the two families above
///     fits. Render 412; the fix is to re-read and retry.
///     </description>
///   </item>
///   <item>
///     <term><see cref="AlvoIdempotencyConflictException"/></term>
///     <description>
///     <b>An idempotency key was reused for a different request.</b> Same
///     <see cref="AlvoIdempotency.Key"/>, different <see cref="AlvoIdempotency.Fingerprint"/>: answering with
///     the first row would silently discard the second payload, and creating a second row would break the
///     promise the key exists to make. The payload itself is well-formed, so this is not the malformed-query
///     channel. Render 409; the fix is a fresh key, not a corrected body.
///     </description>
///   </item>
///   <item>
///     <term><see cref="AlvoConstraintViolationException"/></term>
///     <description>
///     <b>The request collides with stored state the database itself guards.</b> A value another record
///     already holds on a <c>unique</c> field, or a delete a <c>ref</c> declaring <c>onDelete: "restrict"</c>
///     refuses. The payload is well-formed and every facet an implementation can check itself has already
///     passed, so this is neither the malformed-query channel nor a policy refusal; what is wrong is the
///     request's relationship to rows the caller may not even be able to see. Render 409, naming the fields
///     the exception carries — see its own remarks for what may and may not appear there.
///     <para>
///     <b>An implementation must not let the provider's exception escape as this family's neighbour below.</b>
///     Rendered as a 500 it tells the caller nothing they can act on, invites a retry that cannot succeed, and
///     pages an operator for an ordinary mistake. Provider-specific decoding belongs behind the driver's own
///     SQL seam — a constraint's kind is engine-specific (an SQLSTATE, an extended result code, an error
///     number) and must not be recovered by pattern-matching an exception's message in a <c>catch</c>.
///     </para>
///     <para>
///     <b>A collision confined to framework-managed columns is not this family.</b> A caller cannot change
///     <c>id</c> or <c>tenant_id</c>, so a conflict on those alone is an invariant the implementation relies
///     on, and it belongs below with its stack trace intact.
///     </para>
///     </description>
///   </item>
///   <item>
///     <term><see cref="InvalidOperationException"/>, and <see cref="ArgumentNullException"/></term>
///     <description>
///     <b>An invariant the implementation itself relies on is broken</b> — a schema this port cannot serve, a
///     field the read model does not map, a bound value with no known origin. Never caused by a
///     well-formed request from an authorized caller. Render 500.
///     <para>
///     <see cref="ArgumentNullException"/> belongs <em>here</em> rather than to the malformed-query family it
///     derives from, because no request can express a null argument — the first row's own aside carries the
///     full reasoning. It is named in both rows on purpose: an implementer arrives at this table from
///     whichever family their exception is in, and the exclusion was findable from one direction only.
///     </para>
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
/// <b>A write's two concurrency channels, and where each one is decided.</b> An
/// <see cref="AlvoPrecondition"/> is the caller's claim about the version they are changing, and an
/// <see cref="AlvoIdempotency"/> token is their claim that this write may already have happened. Both are
/// optional, and an implementation must honour three rules about them:
/// </para>
/// <list type="bullet">
///   <item>
///   <b>The precondition is compared inside the write transaction, against the row-locked pre-image</b> the
///   <c>WITH CHECK</c> verdict is already reached over — never against a row read on a second, earlier trip.
///   That is what stops the comparison racing the write it guards: between an unlocked read and the write, a
///   concurrent writer can advance the row and the precondition would have approved a lost update. No second
///   read is needed or permitted; the pre-image is already there.
///   </item>
///   <item>
///   <b>An entity with no version column refuses a precondition rather than ignoring it</b>
///   (<see cref="AlvoPreconditionFailedException"/>, via <see cref="AlvoPrecondition.EnsureSupported"/>). A
///   silently ignored <c>If-Match</c> is a lost update the caller believes it prevented — the worst of the
///   three possible answers, because nothing tells them it happened. Decided from the schema alone, before
///   any row lookup, so it cannot answer "does this row exist" either.
///   </item>
///   <item>
///   <b>Invisibility outranks the precondition.</b> A row the caller's <c>USING</c> predicate excludes raises
///   <see cref="AlvoRecordNotFoundException"/> whichever precondition was supplied — never
///   <see cref="AlvoPreconditionFailedException"/>. Ordered the other way round, "412 rather than 404" would
///   confirm that a row exists to a caller who may not read it, one request at a time, which is precisely the
///   oracle the failure contract above exists to close.
///   </item>
/// </list>
/// <para>
/// <b>An idempotency record stores the ids of the rows the write touched, and a replay re-reads them under
/// a freshly resolved <c>get</c> decision for the replaying caller</b> — reading <em>and</em> masking
/// through it. A replayed delete is the one that reads nothing: its rows are gone by construction, so the
/// answer is the same "it is gone" the first call gave, produced without a read. Not
/// under the <c>create</c> decision the call arrived with, and the reason is the one a future implementer has
/// to know rather than rediscover: a <c>create</c> decision has no <c>USING</c> predicate by contract
/// (<see cref="PolicyDecision.Using"/> is <see langword="null"/> — there is no stored row to filter when the
/// decision is made), and a null <c>USING</c> renders as a constant true, so a create decision must never be
/// used to read a stored row. Reading under <c>get</c> is what makes a replay unable to hand back a row the
/// caller could not read directly, or a projection their own <c>hidden</c> set would not produce.
/// </para>
/// <para>
/// <b>A caller whose <c>get</c> is denied outright is not refused a replay — the answer is <c>id</c> alone,
/// with no row read performed</b>: see <see cref="CreateAsync"/>'s <c>idempotency</c> parameter and return
/// value for the safety argument. The
/// case that still refuses is narrower — a <em>configured</em> <c>get</c> whose own predicate excludes this
/// specific row — and it answers <see cref="AlvoRecordNotFoundException"/>, indistinguishable from a row
/// that was genuinely deleted, exactly as any other excluded read does.
/// </para>
/// <para>
/// The record's identity is the caller's key plus a <b>scope of (tenant, acting user)</b> — see
/// <see cref="AlvoIdempotency.IdentityOf"/> for why the user belongs in it and why that is identity rather
/// than a column beside it. An anonymous caller has no identity to scope by, so a token from one is refused
/// outright (<see cref="AlvoIdempotency.EnsureUsableKey"/>).
/// </para>
/// <para>
/// <b>The returned key set and CLR types are part of the contract, not an implementation detail.</b>
/// A returned <see cref="AlvoRecord"/> carries every non-hidden field the schema declares for that
/// entity, including framework-managed columns (<c>id</c>, and — on a tenant-scoped entity —
/// <c>tenant_id</c>); masking removes only descriptor-declared <c>hidden</c> fields, never a
/// framework column.
/// <b><see cref="AlvoQuery.Select"/> is the one other thing that narrows this key set, and it never
/// narrows it below two groups.</b> A projected read returns the fields the projection named, plus
/// every column <see cref="Schema.AlvoManagedColumns.For(Schema.EntitySchema)"/> reports for the
/// entity — the row key alone is what a keyset cursor is minted from — plus every field named in
/// <see cref="AlvoQuery.Sort"/>, because no implementation can order by a column it did not read. Both
/// exemptions are contract, not courtesy: a caller reading "the fields I selected" and receiving
/// those plus a sort key has not been surprised, and one that received *fewer* would have lost its
/// paging. Masking remains the only thing that removes a field the caller did ask for.
/// Field values use the same CLR types <see cref="AlvoRecord"/>'s own remarks
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
    /// is <see langword="null"/> unless <see cref="AlvoQuery.IncludeTotalCount"/> asked for it, and when it did it
    /// counts the <b>policy-filtered</b> set narrowed by the caller's filter — never the table, and never this
    /// page: an implementation composes the count over the same <c>WHERE</c> terms as the page and drops the
    /// ordering, the window and the cursor boundary.
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
    /// <param name="idempotency">
    /// The caller's idempotency token, or <see langword="null"/> for an ordinary create. With a token, the
    /// first create is recorded against it and a replay carrying the same
    /// <see cref="AlvoIdempotency.Fingerprint"/> returns that same row — re-read under a freshly resolved
    /// <c>get</c> decision for the replaying caller, never under this <c>create</c> decision — and writes
    /// nothing. When no policy allows <c>get</c> at all, the replay is not refused: it answers with the id
    /// alone, taken from the recorded key and never from a row read, because a match on the record's identity
    /// already proves this caller created that row. The record is scoped to the caller's tenant <em>and</em>
    /// user, and a token from an anonymous caller is refused, because there is no identity to scope it by.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The created row, with every <c>hidden</c> field stripped — or, on a replay whose caller's <c>get</c> is
    /// denied outright, an <see cref="AlvoRecord"/> carrying only <c>id</c>, with no row read performed. See
    /// <paramref name="idempotency"/> and <c>EfAlvoData.ReplayedAsync</c>'s remarks for the safety argument.
    /// </returns>
    /// <remarks>
    /// <b>The row this returns is the row the store holds</b>, re-read inside the write transaction, not the
    /// payload that was sent: that is what gives a database default, a framework-assigned audit value, and
    /// therefore a usable version a following <see cref="AlvoPrecondition"/> can carry. The id-only replay
    /// answer above is the one exception, and it is deliberate: it never reads the row at all.
    /// </remarks>
    /// <exception cref="AlvoAuthorizationException">
    /// No policy allows <c>create</c> on this entity for <paramref name="context"/>,
    /// <paramref name="values"/> supplies <c>id</c> (always rejected — see the type remarks),
    /// the candidate row fails its <c>WITH CHECK</c> predicate, or <paramref name="values"/> writes
    /// a field the policy marks read-only. <c>tenant_id</c> is not rejected here — see the type
    /// remarks — but a value that fails the tenant scope still raises this exception via
    /// <c>WITH CHECK</c>.
    /// </exception>
    /// <exception cref="AlvoIdempotencyConflictException">
    /// <paramref name="idempotency"/>'s key was already used for a request with a different fingerprint.
    /// </exception>
    /// <exception cref="AlvoConstraintViolationException">
    /// <paramref name="values"/> supplies a value another record already holds on a <c>unique</c> field.
    /// </exception>
    /// <exception cref="AlvoRecordNotFoundException">
    /// <paramref name="idempotency"/> replays a create whose row no longer exists, or is excluded by a
    /// <em>configured</em> <c>get</c> rule's own predicate for <paramref name="context"/> — an entity whose
    /// rule is <c>USING (status == 'published')</c>, say. A replay re-reads rather than returning a cached
    /// body, so a row that has since been deleted or moved out of reach reads exactly as it would on any other
    /// read. This does <b>not</b> apply when no policy allows <c>get</c> at all: see the id-only answer above.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="idempotency"/> is supplied for an anonymous <paramref name="context"/>. Every anonymous
    /// caller carries the same reserved all-zero <see cref="UserId"/>, so their keys would share one space and
    /// one caller's replay could reach another's record — see
    /// <see cref="AlvoIdempotency.EnsureUsableKey"/>. Decided from the token and the context alone,
    /// before any policy is resolved, so it discloses nothing about the entity.
    /// </exception>
    Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);

    /// <summary>Updates a row by id with a partial set of field values.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The row id.</param>
    /// <param name="values">
    /// The field values to change; a field this dictionary does not mention keeps its stored
    /// value — <c>WITH CHECK</c> is evaluated over the complete post-image (the stored row merged
    /// with these values), never over <paramref name="values"/> alone.
    /// </param>
    /// <param name="context">The caller performing the update.</param>
    /// <param name="precondition">
    /// The version the caller believes the row holds, or <see langword="null"/> to write unconditionally.
    /// Compared against the row-locked pre-image inside the write transaction — see the type remarks for the
    /// ordering rules, which are part of the contract.
    /// </param>
    /// <param name="idempotency">
    /// The caller's idempotency token, or <see langword="null"/> for an ordinary write. With a token, the
    /// first write is recorded against it and a replay carrying the same
    /// <see cref="AlvoIdempotency.Fingerprint"/> is answered without writing again — by re-reading the recorded row under a
    /// freshly resolved <c>get</c> decision, exactly as a replayed create is.
    /// The record is scoped to the caller's tenant and user, and a token from an anonymous caller is refused.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The updated row, with every <c>hidden</c> field stripped.</returns>
    /// <exception cref="AlvoRecordNotFoundException">
    /// The row does not exist, or the caller's policy <c>USING</c> predicate excludes it — whichever
    /// <paramref name="precondition"/> was supplied, because invisibility outranks the precondition.
    /// </exception>
    /// <exception cref="AlvoPreconditionFailedException">
    /// <paramref name="precondition"/> does not match the stored row's version, or this entity keeps no
    /// version of a row at all.
    /// </exception>
    /// <exception cref="AlvoAuthorizationException">
    /// No policy allows <c>update</c> on this entity for <paramref name="context"/>;
    /// <paramref name="values"/> supplies <c>id</c> or <c>tenant_id</c> (both always rejected on
    /// update — see the type remarks — and checked against the payload before <paramref name="id"/>
    /// is looked up, so this can never be used to probe whether a row exists); the post-image fails
    /// its <c>WITH CHECK</c> predicate; or <paramref name="values"/> writes a field the policy marks
    /// read-only.
    /// </exception>
    /// <exception cref="AlvoConstraintViolationException">
    /// <paramref name="values"/> supplies a value another record already holds on a <c>unique</c> field.
    /// </exception>
    /// <exception cref="AlvoIdempotencyConflictException">
    /// <paramref name="idempotency"/>'s key was already used for a request with a different fingerprint.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="idempotency"/> is supplied for an anonymous <paramref name="context"/> — see
    /// <see cref="AlvoIdempotency.EnsureUsableKey"/>. Decided from the token and the context alone, before
    /// any policy is resolved, so it discloses nothing about the entity.
    /// </exception>
    Task<AlvoRecord> UpdateAsync(string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a row by id.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The row id.</param>
    /// <param name="context">The caller performing the delete.</param>
    /// <param name="precondition">
    /// The version the caller believes the row holds, or <see langword="null"/> to delete unconditionally.
    /// Compared against the row-locked pre-image inside the delete's own transaction, under the same ordering
    /// rules an update follows.
    /// </param>
    /// <param name="idempotency">
    /// The caller's idempotency token, or <see langword="null"/> for an ordinary write. With a token, the
    /// first write is recorded against it and a replay carrying the same
    /// <see cref="AlvoIdempotency.Fingerprint"/> is answered without writing again — by answering that the row is gone
    /// without reading anything, because there is nothing left to read.
    /// The record is scoped to the caller's tenant and user, and a token from an anonymous caller is refused.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="AlvoRecordNotFoundException">
    /// The row does not exist, or the caller's policy <c>USING</c> predicate excludes it — whichever
    /// <paramref name="precondition"/> was supplied.
    /// </exception>
    /// <exception cref="AlvoPreconditionFailedException">
    /// <paramref name="precondition"/> does not match the stored row's version, or this entity keeps no
    /// version of a row at all.
    /// </exception>
    /// <exception cref="AlvoAuthorizationException">No policy allows <c>delete</c> on this entity for <paramref name="context"/>.</exception>
    /// <exception cref="AlvoConstraintViolationException">
    /// Another record still references this one through a <c>ref</c> declaring <c>onDelete: "restrict"</c>.
    /// </exception>
    /// <exception cref="AlvoIdempotencyConflictException">
    /// <paramref name="idempotency"/>'s key was already used for a request with a different fingerprint.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="idempotency"/> is supplied for an anonymous <paramref name="context"/> — see
    /// <see cref="AlvoIdempotency.EnsureUsableKey"/>. Decided from the token and the context alone, before
    /// any policy is resolved, so it discloses nothing about the entity.
    /// </exception>
    Task DeleteAsync(string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition = null, AlvoIdempotency? idempotency = null, CancellationToken cancellationToken = default);
}
