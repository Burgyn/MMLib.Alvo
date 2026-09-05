# PR-I — Upsert, and the PUT semantics it needs

**Issue:** [#105](https://github.com/Burgyn/MMLib.Alvo/issues/105)
**Milestone:** F4 — Demo from the start
**Date:** 2026-09-05
**Depends on:** PR-H (#102 + #106, merged as [#195](https://github.com/Burgyn/MMLib.Alvo/pull/195)) — the
idempotency machinery and the two-pass write discipline this design reuses.

---

## 0. What this ships

One route and one port operation:

```
PUT {prefix}/{entity}/{id}
```

*Create-or-replace the row that the path names.* The row is written whole: a field the payload does not
mention does not keep its stored value. That is the property separating this from `PATCH`, and it is what
makes the spec's own acceptance criterion — *"dvakrát rovnaké PUT = ten istý stav"*
(`alvo-specifikacia.md:309`) — mean anything.

Not in scope, each with its reason in §9: batch upsert, a mixed batch, upsert on a natural unique key,
`If-None-Match: *`, and the composite `(tenant_id, id)` primary key that would close §3's residual
cross-tenant oracle.

One thing it does that is not a route: **a primary-key collision on a caller-supplied `id` becomes a
translated `409`** instead of the `500` it is today. §3 says why that is a correction rather than a
loosening.

---

## 1. The decision this issue existed to make

> **May a client supply its own `id`?**

**Yes — in the path, and only in the path.** `PUT {prefix}/{entity}/{id}` may create the row that `{id}`
names. `id` inside a request **body** stays refused on every route, including this one:
`AlvoManagedColumns.IsCallerWritable("id", isUpdate: false)` is not relaxed, and `POST` and the three batch
verbs are untouched.

**Decided by the maintainer**, 2026-09-05, from three options put to him:

| | Shape | Outcome |
|---|---|---|
| **A** | `PUT {entity}/{id}`, `id` in the path only | **chosen** |
| B | `PUT` as replace-only (404 when absent); upsert later on a `unique` field, PostgREST-style | rejected |
| C | Relax `IsCallerWritable`, so `id` may arrive in any create payload | rejected |

The reasoning recorded with the choice: the path is where HTTP already puts a resource's identity
(RFC 9110 §9.3.4 — PUT "requests that the state of the target resource be created or replaced"), so a
caller bringing a UUID is addressing a resource rather than writing a column. The rule that the store owns
the key survives intact for every route that mints one.

**What option B bought, and what choosing A costs, is in §3.** B was the only option with no existence
oracle. That was stated before the choice was made; A was chosen knowing it. This paragraph exists so a
later reader can tell a decision from an oversight.

---

## 2. The port

One new member on `IAlvoData`:

```csharp
Task<AlvoReplaceResult> ReplaceAsync(
    string entity,
    Guid id,
    IReadOnlyDictionary<string, object?> values,
    AlvoContext context,
    AlvoPrecondition? precondition = null,
    AlvoIdempotency? idempotency = null,
    CancellationToken cancellationToken = default);
```

and one new result type:

```csharp
public sealed record AlvoReplaceResult
{
    public AlvoReplaceResult(AlvoRecord Row, bool Created) { … }

    public AlvoRecord Row { get; }
    public bool Created { get; }
}
```

**Why `ReplaceAsync` and not `UpsertAsync`.** Both source documents say *upsert*
(`alvo-specifikacia.md:327`, `baas-analyza.md:106`), so the name is a deliberate deviation. "Upsert" names
the branch and says nothing about what happens to the fields the caller omitted — and PostgREST's own
upsert (`Prefer: resolution=merge-duplicates`) **merges** them, which is the opposite of what this does. The
distinguishing property is replacement, so the name states it; the create half is stated by `Created` on
the result and in the member's own docs. A member named `UpsertAsync` that silently replaced would be a
name working against its reader.

**Why a result type rather than `AlvoRecord` plus an `out`.** The caller has to answer `201` or `200`, and
that answer is not derivable from the record. A get-only record with a validating constructor follows
`AlvoBatchResult`'s precedent — including that lesson: **the members are get-only, not `init`**, so a `with`
expression cannot rebuild a state the constructor refused, and any collection it ever holds is copied.

---

## 3. Branching, and the existence oracle

### How the branch is chosen

Exactly as `UpdateAsync` chooses today, with no new read:

1. Read the pre-image through the policy-scoped, row-locking `FromSql` root (`SingleAsync` with
   `PreImageMutation.Update`), so `USING` and the tenant scope are rendered as the SQL `WHERE` and the row
   is held from read to write.
2. **Row returned → replace branch.**
3. **No row → create branch**, a plain `INSERT`.

### The collision does not answer 409 today, and this PR has to make it

A row that exists but that the caller's `USING` excludes returns nothing from step 1, so the create branch
runs and its `INSERT` collides with the primary key. **Today that is a `500`, deliberately**, and the design
must change it:

- `ConstraintViolationTranslator.CallerFields` strips every framework-managed column and `Translate` returns
  `null` when nothing survives (`ConstraintViolationTranslator.cs:142-149`). A collision on `id` is a
  collision on a managed column and only a managed column, so nothing survives and the raw provider
  exception propagates.
- The **public** contract says so too: *"Framework-managed columns are excluded, because a caller cannot
  change one — a collision confined to them is a broken invariant rather than a conflict, and an
  implementation must let that keep propagating as one"* (`AlvoConstraintViolationException.cs:26-32`).
- Worse, on the idempotent path the untranslated `DbException` matches `IsStorageWriteFailure`
  (`EfAlvoData.cs:486`) and is retried ten times before exhausting — reproducing the #138 defect the same
  file documents.

**The exclusion's own premise expires here.** It says *a caller cannot change one*. On this route the caller
supplies `id`, so a collision on it is an ordinary caller-caused conflict, not a broken invariant. PR-I
therefore teaches the translator that **this write was keyed by the caller**, and on such a write `id`
survives `CallerFields` and the violation translates to `AlvoConstraintKind.Unique` with `Fields: ["id"]`.

No new `AlvoConstraintKind` member: a primary key is a uniqueness constraint, and `id` is a *name* the
caller already sent, which is exactly what the type's own rule permits `Fields` to carry. Nothing changes
for any existing write — on every route that mints its own key the premise still holds and the collision
still propagates as the broken invariant it is. The public remark is amended to say *which* writes it
speaks for, rather than being contradicted by a route it predates.

### The oracle, and the deviation from #137

Once the collision answers `409`, a caller who holds a UUID and may create in this entity learns:

- **409** — a row with that id exists, invisible to them, possibly in another tenant;
- **201** — that id is free.

**The repo has already ruled on this shape of oracle, and against it.** #137 put the tenant column into
every unique index on a scoped entity, and said why:

> "A bare `HasIndex(field).IsUnique()` on a scoped entity enforces uniqueness across the *whole instance* …
> the two requests differ in exactly one thing, whether another tenant holds the value, so any observable
> difference between the answers discloses that fact. That is a cross-tenant existence oracle, and it
> contradicts the premise that Alvo's app-side rules are as safe as native row-level security.
> `(tenant_id, field)` keeps the constraint doing its job *within* a tenant and removes the signal between
> tenants; note that mapping the underlying violation to a clean `409` (#138) does **not** close it, because
> `409`-versus-`201` is the same one-bit signal as `500`-versus-`201`."
> — `DescriptorModelBuilder.cs:64-72`

The primary key is global today — `entityBuilder.HasKey("id")` (`DescriptorModelBuilder.cs:52`),
`builder.HasKey(IdColumn)` (`AlvoDataContext.cs:141`) — so the analogous fix is a composite
`(tenant_id, id)` key, and it would remove the cross-tenant half of the signal outright.

**This is a deliberate deviation, decided by the maintainer on 2026-09-05, and PR-I ships without the
composite key.** The reason is that the two cases differ in what the oracle is *worth*, not in shape:

| | #137 | here |
|---|---|---|
| the value the caller supplies | a natural key — an e-mail, an order number | a UUID |
| can it be guessed? | **yes** — the oracle turns a guess into knowledge | **no** — v4/v7 UUID space is not sweepable |
| what the answer tells them | a fact they did not have | a fact they had to already hold to ask |

#137's harm is *discovery*: an attacker enumerates plausible values and learns which ones some tenant holds.
That path does not exist here — to ask the question at all, the caller must already possess the UUID, which
means they already knew the row existed. The residue is confirmation that a UUID obtained out of band is
still live. Structurally it is the same one bit; in risk it is not the same bit.

The alternatives were weighed and rejected: the composite key is a physical-schema change to every scoped
entity — migrations, foreign keys, the read path — and belongs in its own PR rather than riding along with
a route; and restricting PUT to `tenancy: global` entities would publish a document where the same verb
exists for some entities and not others, which is the inconsistency an agent-first API least affords.

**If the composite key ever lands, this section is what it closes.** Recorded here so the residue is a
tracked decision rather than a discovered one.

**What further narrows it, meanwhile:**

- **PUT requires both `create` and `update`** (§4). A caller who lacks `create` is refused before any row
  is read, so the oracle is unreachable for them.
- The `409` **names no value and no owner** — only the field name `id`, which the caller sent.
- On a `tenancy: global` entity there is no cross-tenant half at all; the residue there is the ordinary
  within-tenant `USING` case.

**Every existing answer is unchanged.** `PATCH` and `DELETE` still answer `AlvoRecordNotFoundException` for
an invisible row, indistinguishable from an absent one. This design adds no path that makes an existing
operation more talkative; it adds a new operation that cannot be silent.

### Deviation: Postgres answers this differently

Postgres' own row-level security makes the opposite call for `INSERT … ON CONFLICT DO UPDATE`:

> "the row to be updated is first checked against the `USING` expressions of any `UPDATE` policies … Note,
> however, that unlike a standalone `UPDATE` command, if the existing row does not pass the `USING`
> expressions, **an error will be thrown** (the `UPDATE` path will *never* be silently avoided)."
> — PostgreSQL 16, `CREATE POLICY`

Postgres refuses loudly on the invisible row rather than falling through to the insert. **Alvo does not
follow it here**, and the reason is Alvo's own failure contract, stated in `IAlvoData`'s remarks: *"A row
that exists but that the caller's policy `USING` predicate excludes must read exactly like a row that was
never there."* Following Postgres would make the replace branch's refusal a second, louder oracle on top of
the unavoidable one, and would do it on the path a legitimate caller uses. The fall-through keeps the
disclosure confined to the primary key, where it cannot be helped.

The `WITH CHECK` half of Postgres' rule **is** followed exactly — see §4.

---

## 4. Policy: both operations, both branches checked

**PUT is gated by `create` *and* `update`.** `ReplaceAsync` resolves both decisions up front and denies
unless both allow. A caller permitted only to update may not reach the create branch by naming an unused
id, and a caller permitted only to create may not reach the replace branch by naming a used one.

**Which `PolicyDecision` does what has to be stated, because getting it wrong is a bypass this repo has
already shipped once.** `PolicyDecision.Using`'s own remarks: *"a `create` decision must never be used to
read a stored row: doing so returns the row whoever owns it, with no predicate at all. That is not
hypothetical — it is the bypass F3 PR3 shipped and then fixed"* (`PolicyDecision.cs:62-69`). So:

| what | which decision |
|---|---|
| `Using` for the locked pre-image read | the **update** decision |
| `WITH CHECK` + tenant scope, replace branch | the **update** decision |
| `WITH CHECK` + tenant scope, create branch | the **create** decision |
| `ReadOnlyFields` for `WritePayloadGuard` | both — a field read-only under either is refused |
| `HiddenFields` masking the response | the branch's **own write** decision, exactly as `CreateAsync` and `UpdateAsync` mask today |

`WritePayloadGuard.EnsureWritable` takes **one** decision (`WritePayloadGuard.cs:52`), so "read-only under
either" is two calls to `PayloadRefusal` and a refusal on the first non-null answer — not a merged set, which
would be a second place the union is computed. Default-deny makes the union the only defensible reading: a
field the `create` rule freezes must not become writable because the row happened to exist.

Masking follows the existing convention rather than the replay's. A replay re-resolves a fresh `get`
decision because it answers a row it did **not** write; a write masks with the decision that authorised it,
which is what `UpdatedAsync` does (`EfAlvoData.cs:955`). Giving this one route a third behaviour would make
"which decision masks a write's response" a per-route question for no gain.

**No new `DataOperation` member.** `DataApiEndpointKind`'s own remarks say why: `DataOperation` is the
*policy* vocabulary that a descriptor's `rules` name and `PolicyCatalog` is keyed by, so a member added
there would let a descriptor configure a rule for a transport. `DataApiEndpointKind.Replace` is a new
*route* kind, and it maps to `DataOperation.Update` wherever one operation is asked for.

**The endpoint filter gates both operations, and it must.** `DataApiEndpoints`' remarks state a symmetric
invariant, not an advisory one: *"nothing is admitted here that the port would refuse, and nothing is
refused here that the port would admit"* (`DataApiEndpoints.cs:44-51`). A filter that checked only `update`
would admit an update-only caller whom the port then refuses — breaking the first half. So the `PUT`
delegate resolves and checks **both** operations up front, and the invariant holds in both directions. This
is the one place `DataApiEndpointKind.Replace` needs more than `ToDataOperation()` can express, and the
delegate is where that is written rather than in the enum.

**`WITH CHECK` runs on the candidate row in both branches**, which is #105's item 1 and the one thing an
upsert must not get wrong:

| | pre-image `USING` | post-image `WITH CHECK` | tenant scope |
|---|---|---|---|
| create branch | — (no row) | ✔ on the candidate | ✔ |
| replace branch | ✔ (the SQL `WHERE` of the locked read) | ✔ on the merged post-image | ✔ |

This is `EnsureWriteAllowed` in both cases — the existing in-process CEL evaluation over the materialized
candidate dictionary, with `previous: null` on create and the locked pre-image on replace. It matches
Postgres' table for `ON CONFLICT DO UPDATE` row for row on the check half.

**A hook never writes a row `WITH CHECK` has not judged**, and the two branches reach that guarantee by the
two different routes the code already uses — which is worth stating precisely, because the two are easy to
describe wrongly:

- **Create branch:** `AuthorizedCandidate` evaluates `WITH CHECK` on the candidate, then `RunBeforeCreate`
  evaluates it **a second time** over the patched post-image when the hook changed anything
  (`EfAlvoData.cs:359` and `:305`).
- **Replace branch:** the hook runs **first** and `WITH CHECK` is evaluated **once**, afterwards, over the
  merged post-image (`WriteAsync`, `EfAlvoData.cs:1329-1331`). `RunBeforeUpdate` contains no evaluation of
  its own — an implementer looking for a re-verdict inside it will not find one, and must not add a
  pre-hook evaluation "for symmetry": that would judge an image the store never sees.

`ReplaceAsync` reuses each branch's existing shape rather than imposing one on both.

---

## 5. `tenant_id`, and the second oracle this design closes

`WritePayloadGuard`'s whole property is that **every refusal is decided from the payload alone**, so a
caller cannot use "was my write rejected" to learn whether a row exists — its own remarks say so.

`tenant_id` is the one column that is caller-writable on a create and refused on an update
(`AlvoManagedColumns.IsCallerWritable`: `!isUpdate && column == TenantId`). If PUT ran the guard with an
`isUpdate` that followed the branch, then *"is my `tenant_id` refused?"* would depend on whether the row
exists — a **new existence oracle, decided from the payload**, deliberately triggerable, and available
before any of §3's narrowing applies.

**So PUT runs `WritePayloadGuard.EnsureWritable` with `isUpdate: true` unconditionally.** `tenant_id` is
never caller-writable on this route, on either branch.

**Which means the framework has to supply the value, and that is the other half of this decision.** The
tenant scope in `WITH CHECK` *checks* the candidate's `tenant_id`; it does not set one, and
`AlvoAuditStamp` deliberately never touches that column. So on a tenant-scoped entity the create branch
would build a candidate with no `tenant_id` at all and be refused by its own scope — the route would simply
not work.

So on this route, and only on the create branch, **`tenant_id` is stamped from the caller's own context**.
The two halves fit: the caller may not name a tenant, and the framework names the only one the scope would
have accepted anyway. On an anonymous caller, or one with no tenant, there is nothing to stamp and the
scope refuses the candidate exactly as it would refuse any other row with no tenant — no special case.

A caller who legitimately needs to create a row **into another tenant** uses `POST`, which still accepts
`tenant_id` and still judges it against the same scope. Nothing that was possible becomes impossible; it
moves to the route that already did it.

Two boundaries on the claim, so it is not read wider than it is. The oracle exists only on a
`tenancy: scoped` entity — on a global one `tenant_id` is not among the entity's managed columns and
`QueryFieldGuard.EnsureDeclared` refuses it identically on both branches, so there is no difference to
observe. And a branch-dependent guard would additionally have to run *after* the pre-image read, which
already contradicts the guard's stated position ("before any row is looked up"); the leak and the
structural violation arrive together.

**Implementation note, because the existing helper cannot express this.** `AuthorizedCandidate` couples the
two `isUpdate` values in adjacent lines — `EnsureWritable(…, isUpdate: false)` then
`Stamped(…, isUpdate: false)` (`EfAlvoData.cs:357-359`). PUT's create branch needs `isUpdate: true` for the
guard (this section) and `isUpdate: false` for the stamp (§7), so it cannot reuse `AuthorizedCandidate` as
written. The two arguments are separated — the guard is told whether the *caller* may write the column, the
stamp is told whether the *row already exists* — and they stop being one flag that happens to answer both.

---

## 6. Replacement semantics for the fields the caller omits

This is #105's item 3, and the state of `field.default` has to be stated exactly, because it is neither
"shipped" nor "absent":

- **The descriptor schema declares it.** `schema/project.schema.json:621` defines `default` on `$defs/field`
  as a JSON literal or a tagged `{"$cel": "…"}` expression "evaluated at insert time", and two conditional
  branches already forbid it beside `computed`/`rollup`. **A descriptor may write it today.**
- **Nothing reads it.** `FieldSchema` has no `Default` member, so the mapper drops it silently. Implementing
  it is [#113](https://github.com/Burgyn/MMLib.Alvo/issues/113), still open.

So a declared default is currently inert on *every* path, PUT included — there is no stored default for a
replacement to fall back to, and a `required` field with a declared default is, as far as the runtime is
concerned, a `required` field with nothing. The table below is written against that reality rather than
against the schema's promise. (That a valid descriptor can declare a default which is silently dropped is a
defect in its own right, and it belongs to #113 rather than here; PR-I notes it on that issue.)

| The omitted field is… | PUT writes |
|---|---|
| framework-managed (`id`, `tenant_id`, `created_at`, `created_by`) | preserved on replace, stamped on create — never nulled |
| `updated_at` / `updated_by` | stamped, as on any write |
| `computed` | nothing; the engine owns a stored generated column |
| a descriptor field that is nullable | `null` |
| a descriptor field that is `required` | **nothing is written — the request is refused, `422`, naming the field** |

**Why `required` is refused rather than preserved.** Preserving the stored value is what `PATCH` does, and
a `PUT` that quietly did it would be a `PATCH` wearing another verb's name — RFC 9110 §9.3.4 says PUT
replaces the target resource's state with the enclosed representation, and a representation that leaves a
field to whatever was already there has not replaced anything. Two identical PUTs from two different
starting states would then produce two different rows. The refusal names the field and suggests `PATCH`,
per principle 4 (structured errors with fix suggestions).

The spec's *"dvakrát rovnaké PUT = ten istý stav"* (`alvo-specifikacia.md:309`) is consistent with this but
does **not** decide it: read literally it is the same PUT applied twice, which a merge satisfies too — PATCH
is idempotent in exactly that sense. The criterion is why §10 asserts the property from **two different
starting states**; RFC 9110 is what makes the answer replacement rather than merge.

**The `required` + `hidden` corner is stated, not papered over.** `data-api.md` already establishes that a
mandatory secret — a password, an API token the caller supplies and can never read back — is exactly
`required: true` + `hidden: true`. Such a field cannot be restated by a caller who cannot read it, so an
entity carrying one is **not PUT-able** by that caller. The `422` says so in as many words and points at
`PATCH`. Inventing a "keep the hidden one" exception would reintroduce the partial-update semantics this
route exists not to have.

**Forward commitment for #113.** When `field.default` lands, a declared default becomes the answer for an
omitted field *ahead of* `null`, and a `required` field **with** a default stops being a `422`. That is a
change to this route's contract, so #113 must revisit this table rather than land beside it. Recorded here
so the interaction is a decision in both directions.

---

## 7. Audit stamp, idempotency, preconditions, events

**Audit stamp.** `AlvoAuditStamp.Applied` already does the right thing per branch: called with
`isUpdate: false` it sets `created_*` and `updated_*` to the same instant and actor; with `isUpdate: true`
it sets only `updated_*`. The replace branch calls it with `isUpdate: true`, so `created_at`/`created_by`
survive a replacement — a replaced row is the same row, not a new one. The ordering contract is unchanged:
guard → stamp → `WITH CHECK`, so a rule like `created_by == @user.id` is satisfied by the stamp and never
by a caller-supplied claim.

**Idempotency.** `Idempotency-Key` is accepted and behaves exactly as PR-H made it behave on every other
write: the record is looked up inside the write's own transaction, a different fingerprint for the same key
is `AlvoIdempotencyConflictException` (409), a replay re-resolves a **fresh `get` decision for this caller**
and answers the row through it — or `id` alone when `get` is denied outright. The record is inserted last,
after the row write and after the outbox emit, so a rival losing the primary-key race rolls back the write
and the event together. Contention retries reuse `ReplayableWriteAsync` with the single-row
`ContendedCreateAttempts`.

**A replay answers `200`, never `201`**, and emits no `Location`. The idempotency record carries row ids,
not the branch that wrote them, and the two ways to recover the branch are both worse than not needing it:
storing it grows the record for one header, and inferring it from `created_at == updated_at` is a guess
that a row replaced in the instant it was created defeats. Neither is necessary, because `201` reports an
act of creation and a replay performs none — it reports the state a previous request left. This is stated
in the member's own docs and pinned by a test.

**Preconditions.** `If-Match` works as it does on `PATCH`, on the replace branch, through the same
`AlvoPrecondition.EnsureMatches` against the version read off the locked pre-image. The ordering the
codebase already fixed is preserved: **invisibility → precondition → `WITH CHECK`**. On the create branch
an `If-Match` cannot match anything, so it answers `412` — a precondition that names a version is a
precondition that the resource exists.

**Events.** The branch emits **its own** event type: `entity.{entity}.created` on the create branch,
`entity.{entity}.updated` on the replace branch. **No new `replaced` type**, because a consumer subscribed
to `entity.x.updated` must not silently miss every replacement — and because a new type is a new thing every
existing subscriber has to learn. `Data.OldRecord` is `null` on the create branch and the pre-image on the
replace branch, and `Data.Changed` follows the existing rule (every field on a create, only the differing
fields on an update), so an existing consumer needs no change at all.

---

## 8. HTTP surface

| Situation | Answer |
|---|---|
| created | `201` + `Location` + the row + `ETag` |
| replaced | `200` + the row + `ETag` |
| replayed from an `Idempotency-Key` | `200` + the row (never `201`) |
| caller lacks `create` or `update`, or `WITH CHECK` refuses | `403` |
| an omitted `required` field, or a body that fails validation | `422`, naming the field |
| `If-Match` mismatched, or `If-Match` on the create branch | `412` |
| the id is taken by a row the caller cannot see | `409` |
| a unique or reference constraint | `409`, as today |
| same `Idempotency-Key`, different body | `409` |

Every status above is rendered by an existing `ProblemResultFactory` arm and no new problem type is minted.
**One of them does not reach its arm today**: "the id is taken by a row the caller cannot see" needs the
translator change in §3, without which it is a `500` and, on the idempotent path, ten retries first. That is
work this PR does, not a status it inherits.

**The route is `PUT` on the existing `item` pattern** (`{prefix}/{entity}/{{id:guid}}`), beside `GET`,
`PATCH` and `DELETE`.

**Five switches must be extended with the new kind, and two of them fail silently.** This is a hard
implementation note, not a reminder.

*The loud ones.* `DataApiEndpointKind.Replace` needs its own `ToWireName()` arm (`"replace"`) or its
`operationId` collides with `update`'s and one route's prose is published for the other; and
`DataApiDocumentation`'s three switches (`:158`, `:344`, `:366`) must each gain an arm, because
`AlvoEndpointDataSource.BuildOrRefuseToRoute` catches the resulting `InvalidOperationException` and **every
route in the document disappears** — 229 tests went red on exactly this during PR-H.

*The silent ones, in `DataApiParameters.cs`, and these are the dangerous half:*

- `AddressesOneRow` (`:164`) is an `is` pattern listing `Get`/`Update`/`Delete`. A kind missing from it is
  simply `false`, so the published `PUT` **declares no `{id}` path parameter** while its own path template
  has one.
- `HeaderNames` (`:232-243`) is a `switch` whose default arm is `[]`. A kind missing from it publishes
  **no `If-Match` and no `Idempotency-Key`**, silently contradicting §7 — which is exactly the bug PR-H
  shipped and had to fix for the batch kinds.

Neither throws, so `BuildOrRefuseToRoute` never sees them and the route-count canary stays green. They are
caught only by a test that asserts the published parameters and headers for `PUT` by name.

---

## 9. Alternatives rejected

- **`If-None-Match: *`** (RFC 9110 §13.1.2 — "create only if absent"). It is the standard way to make a PUT
  safe against creating what someone else just created, and it is genuinely useful here. It is deferred
  because it is a second precondition family — `AlvoPrecondition` models a version match today, not an
  existence assertion — and this PR's risk budget is spent on the two-branch policy evaluation, which is
  the part that is catastrophic if wrong. A follow-up issue.
- **Batch upsert / a mixed batch** — one body carrying creates, updates and deletes together.
  `data-api.md` deferred the mixed batch to #105 precisely so this decision could be made first; it has now
  been made, and the mixed batch is a separate shape built on top of it. A follow-up issue, so the
  single-row semantics can be reviewed on their own.
- **Upsert on a natural unique key** (PostgREST's `on_conflict=` / `Prefer: resolution=merge-duplicates`).
  This was option B's second half. It remains the better answer for a caller who owns an external key
  (`order_no: "SO-1234"`) and has no UUID, and the descriptor already supports `unique: true` on a field, so
  nothing here forecloses it. A follow-up issue.
- **Requiring `id` in the body as well as the path**, which is what PostgREST's PUT does ("All the columns
  must be specified in the request body, including the primary key columns"). Its own documentation does not
  say what happens when the two disagree — a real gap in the prior art. Alvo removes the disagreement by
  construction: `id` lives in the path, and in the body it is refused with the message it is already
  refused with everywhere else.
- **A composite `(tenant_id, id)` primary key**, the analogue of #137's fix for unique indexes. It would
  remove the cross-tenant half of §3's oracle outright, and it is the right eventual answer — but it
  rewrites the physical key of every scoped entity, with the migrations, foreign keys and read path that
  implies, and that does not belong inside a PR whose subject is a route. §3 records the deviation and the
  maintainer's decision; a follow-up issue carries the key.
- **Restricting PUT to `tenancy: global` entities** until that key lands. It removes the cross-tenant oracle
  by removing the tenants, and publishes a document in which the same verb exists for some entities and not
  others — the inconsistency an agent-first API can least afford.
- **A new `DataOperation.Replace`.** §4: it would let a descriptor write a rule for a transport, and would
  make "`update` is unconfigured" stop answering for a route that updates.
- **A new `AlvoConstraintKind` for a primary-key collision.** §3: a primary key *is* a uniqueness
  constraint, and `Unique` with `Fields: ["id"]` already says everything the caller can act on. A second
  member would give two names to one condition.
- **A new `entity.x.replaced` event type.** §7: it would make every existing `updated` subscriber silently
  incomplete.
- **Deciding the branch with an unfiltered read**, so an invisible row could answer `404` like `PATCH`. It
  moves the oracle from `409`-vs-`201` to `404`-vs-`201` without removing it (§3), and it would put a read
  that deliberately ignores the policy predicate into the security core — a shape worth not having for a
  benefit that is zero.

---

## 10. Testing

The suite has to answer one question above all others: **does `WITH CHECK` run on the candidate row in
both branches?** An upsert that checks the update branch and lets the create branch through is a policy
bypass, and it is the failure this design is most likely to have.

- **Inherited adversarial suite.** `ReplaceAsync` joins `AlvoDataAdversarialTests`, so `InMemoryAlvoData`,
  SQLite and PostgreSQL are held to one contract. A rule that refuses the candidate refuses it on **both**
  branches; the pin fails if either branch skips the check.
- **The oracle is pinned as designed behaviour**, not left to chance: a test asserts that a PUT on an id
  held by a row outside the caller's `USING` answers `409`, and one asserts that a caller lacking `create`
  gets `403` for that same id — i.e. that the narrowing in §3 actually narrows.
- **The translator change is fenced on both sides.** A caller-keyed collision on `id` translates to
  `AlvoConstraintKind.Unique` with `Fields: ["id"]` and renders `409`; a collision on a **framework-minted**
  id — every other write path — still propagates untranslated, because it is still the broken invariant the
  existing remark describes. A third test pins that the idempotent PUT does **not** burn ten retries on it,
  which is the #138 shape the untranslated exception would otherwise re-enter.
- **`tenant_id` is refused on both branches** (§5), with a test that would fail if the guard's `isUpdate`
  ever followed the branch.
- **Replacement semantics**: a nullable omitted field becomes `null`; a `required` omitted field is `422`
  naming the field; `created_at`/`created_by` survive a replace; a `computed` field is never written.
- **Idempotence, as the spec words it**: the same PUT applied twice leaves the same stored state — asserted
  from **two different starting states**, since one starting state cannot tell replacement from a merge.
- **A replay answers `200`, not `201`**, and emits no `Location`.
- **Every test is verified by injection**, not by watching it pass: the check is removed, the branch is
  crossed, the guard's `isUpdate` is flipped — and the test must go red for the stated reason. PR-H found
  three vacuous tests this way; the same discipline applies here.
- **E2E**: the field-service TeaPie suite gains a PUT folder. `access_code` on `work_orders` is
  `required` **and** `hidden`, which makes it the natural place to pin §6's un-PUT-able case rather than a
  synthetic entity.

---

## 11. Public API delta, and why each symbol

`public` is the contract, so each addition is argued rather than assumed. The `turn-review-gate` hook fires
on any `PublicApi.*.verified.txt` that grew, so this list is what that check will be answered with:

| Symbol | Baseline | Why it must be public |
|---|---|---|
| `IAlvoData.ReplaceAsync` | Abstractions | the port is the published contract; a provider implements it |
| `AlvoReplaceResult` | Abstractions | it is that member's return type |
| `AlvoReplaceResult.Row` / `.Created` | Abstractions | the caller cannot answer `201` vs `200` without `Created` |
| the `record`'s synthesized members | Abstractions | `EqualityContract`, `PrintMembers`, `ToString`, `Equals`, `GetHashCode`, `op_Equality`/`op_Inequality`, the copy constructor — the cost of `record`, paid identically by `AlvoBatchResult` |
| `InMemoryAlvoData.ReplaceAsync` | Testing | the reference implementation is `public sealed` and implements the port; a new interface member forces it |
| each new `AlvoDataAdversarialTests` fact | Testing | the class is `public abstract` and every implementation's suite inherits it — that is how one contract is held across three drivers |

`DataApiEndpointKind.Replace` stays `internal`, as its enum already is; the endpoint mapping, the branch
selection, the guard call and the translator's caller-keyed flag are all `internal` to their packages.

**Adding a member to `IAlvoData` is source- and binary-breaking for any out-of-tree provider**, and this
design does it without a default implementation on purpose: a port member with a default body would let a
provider silently *not* implement create-or-replace and still compile, which for a security-core port means
a provider that answers a write with whatever the default did. Nothing is released — the repo has no tags
and no published packages — so the break costs nobody today, and that is the cheapest moment to take it.
