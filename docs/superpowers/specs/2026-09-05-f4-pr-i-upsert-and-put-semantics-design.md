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
`If-None-Match: *`.

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

### The oracle, stated

A row that exists but that the caller's `USING` excludes returns nothing from step 1, so the create branch
runs and its `INSERT` collides with the primary key. `ConstraintViolationTranslator` turns that into
`AlvoConstraintViolationException`, which `ProblemResultFactory` already answers as **409**.

So a caller who holds a UUID and may create in this entity learns:

- **409** — a row with that id exists somewhere, invisible to them;
- **201** — that id is free.

**This is inherent, not an implementation slip.** A primary key cannot collide silently, so no arrangement
of an id-addressed create-or-replace avoids it. Answering `404` for the invisible row instead of `409`
relabels the oracle, it does not remove it: `404` versus `201` distinguishes the same two states. The only
option that removed it was B, which never creates.

**What narrows it:**

- **PUT requires both `create` and `update`** (§4). A caller who lacks `create` is refused before any row
  is read, so the oracle is unreachable for them.
- The caller must **already hold the UUID**. Ids are v4/v7 UUIDs and Alvo treats them as identifiers, not as
  secrets — nothing in the codebase relies on an id being unguessable — but they are not enumerable either,
  so the oracle answers a question the caller had to bring rather than one they can sweep for.
- The `409` **names no value and no owner**. `data-api.md` already rejected reporting a batch's `409` with
  the offending row index because unique values are guessable; here the only value involved is the
  caller's own id, so the body adds nothing they did not send.

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

**No new `DataOperation` member.** `DataApiEndpointKind`'s own remarks say why: `DataOperation` is the
*policy* vocabulary that a descriptor's `rules` name and `PolicyCatalog` is keyed by, so a member added
there would let a descriptor configure a rule for a transport. `DataApiEndpointKind.Replace` is a new
*route* kind; `ToDataOperation()` maps it to `DataOperation.Update` for the endpoint's early advisory
filter, and the port — the sole authority, as `DataApiEndpoints`' own remarks state — requires both. The
filter can only under-refuse relative to the port, never over-refuse.

**`WITH CHECK` runs on the candidate row in both branches**, which is #105's item 1 and the one thing an
upsert must not get wrong:

| | pre-image `USING` | post-image `WITH CHECK` | tenant scope |
|---|---|---|---|
| create branch | — (no row) | ✔ on the candidate | ✔ |
| replace branch | ✔ (the SQL `WHERE` of the locked read) | ✔ on the merged post-image | ✔ |

This is `EnsureWriteAllowed` in both cases — the existing in-process CEL evaluation over the materialized
candidate dictionary, with `previous: null` on create and the locked pre-image on replace. It matches
Postgres' table for `ON CONFLICT DO UPDATE` row for row on the check half.

**The before-hook re-verdict is kept.** If a `beforeCreate`/`beforeUpdate` hook patches the candidate,
`WITH CHECK` is evaluated **again** over the patched post-image, exactly as `RunBeforeCreate` and
`RunBeforeUpdate` do today. A hook that could move a row past a rule the caller could not is the same hole
in a new place.

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

The consequence is stated rather than hidden: a PUT that creates places the row in the caller's own tenant,
because the synthesized tenant scope in `WITH CHECK` is what decides the tenant and the payload no longer
gets a say. A caller who legitimately needs to create a row **into another tenant** uses `POST`, which still
accepts `tenant_id` and still judges it against the same scope. Nothing that was possible becomes
impossible; it moves to the route that already did it.

---

## 6. Replacement semantics for the fields the caller omits

This is #105's item 3, and it is decided **without** `field.default`, which does not exist —
`FieldSchema` has no `Default` member and the descriptor schema declares none. It is
[#113](https://github.com/Burgyn/MMLib.Alvo/issues/113), still open.

| The omitted field is… | PUT writes |
|---|---|
| framework-managed (`id`, `tenant_id`, `created_at`, `created_by`) | preserved on replace, stamped on create — never nulled |
| `updated_at` / `updated_by` | stamped, as on any write |
| `computed` | nothing; the engine owns a stored generated column |
| a descriptor field that is nullable | `null` |
| a descriptor field that is `required` | **nothing is written — the request is refused, `422`, naming the field** |

**Why `required` is refused rather than preserved.** Preserving the stored value is what `PATCH` does. A
`PUT` that quietly did it would make two identical PUTs from two different starting states produce two
different rows, which is precisely the acceptance criterion at `alvo-specifikacia.md:309`
(*"dvakrát rovnaké PUT = ten istý stav"*) inverted. The refusal names the field and suggests `PATCH`, per
principle 4 (structured errors with fix suggestions).

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

Every status above is produced by an existing `ProblemResultFactory` arm. No new problem type is minted,
and no existing one changes meaning.

**The route is `PUT` on the existing `item` pattern** (`{prefix}/{entity}/{{id:guid}}`), beside `GET`,
`PATCH` and `DELETE`.

**Three switches must be extended with the new kind, and this is a hard implementation note, not a
reminder.** `DataApiEndpointKind.Replace` needs its own `ToWireName()` arm (`"replace"`) or its
`operationId` collides with `update`'s and one route's prose is published for the other; and
`DataApiDocumentation`'s three switches must each gain an arm, because
`AlvoEndpointDataSource.BuildOrRefuseToRoute` catches the resulting `InvalidOperationException` and **every
route in the document disappears** — 229 tests went red on exactly this during PR-H.

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
- **A new `DataOperation.Replace`.** §4: it would let a descriptor write a rule for a transport, and would
  make "`update` is unconfigured" stop answering for a route that updates.
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

`public` is the contract, so each addition is argued rather than assumed:

| Symbol | Why it must be public |
|---|---|
| `IAlvoData.ReplaceAsync` | the port is the published contract; a provider implements it |
| `AlvoReplaceResult` | it is that member's return type |
| `AlvoReplaceResult.Row` / `.Created` | the caller cannot answer `201` vs `200` without `Created` |

Nothing else. `DataApiEndpointKind.Replace` is `internal`, as its enum already is; the endpoint mapping,
the branch selection and the guard call are all `internal` to the core.
