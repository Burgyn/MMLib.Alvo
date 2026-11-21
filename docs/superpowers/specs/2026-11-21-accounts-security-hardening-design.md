# End-user accounts, hardening: lock order and a banned sole owner, undoing a stolen session, signing-key rotation, the sweep — design

**Status:** design approved — every §9 question ruled on the maintainer's behalf, scope ruled (§9.2: #543, #544 `azp`,
#527 now; #544 rotation and #546 deferred) (2026-11-21); #543 (slices 1 and 3)
implemented on this branch; every slice is `needs-deep-review` · **Date:** 2026-11-21 ·
**Branch:** `debt/p3-accounts-hardening-design` · **Issues:** [#543](https://github.com/Burgyn/MMLib.Alvo/issues/543),
[#546](https://github.com/Burgyn/MMLib.Alvo/issues/546), [#544](https://github.com/Burgyn/MMLib.Alvo/issues/544),
[#527](https://github.com/Burgyn/MMLib.Alvo/issues/527) (milestone F6 — v0.1)

Built on the accounts spec `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md`
(§4 D6/D9/D10, §5.5, §5.6, §6.4, §7.1, §14), on the contributor page `docs/architecture/accounts.md` (*Deviations*, S3
D3/D10, S4 D6/D16, S5 D10, *The stack review's fixes*), on `docs/product/baas-analyza.md` §2.2 (its acceptance criteria
are quoted where a decision answers to them), and on the code at `origin/main` `6a71769f`. Every `file:line` below is
at that commit.

## 1. Intent

The whole-stack review of the accounts initiative left four security follow-ups. None breaks the tenant wall. Each
leaves either an operator without a way out (#543), a thief with power that outlives the theft (#546), a key with no
way to change it (#544), or tables that only grow (#527). They are designed together because they share two
foundations that no single issue owns:

- **One lock order for every write that touches a person's credentials** (§2.3). #543 asks for it. #546's undo and
  #527's sweep both add new multi-row writes over the same rows, so they must be built on top of it.
- **Knowing which session made what** (§3.4). #546 needs it to undo exactly what a stolen session did. #527's sweep
  then may delete the invitation rows that #546 would otherwise have to read.

Alvo is pre-v0.1 with no users (spec §1; memory *No users yet*). Every recommendation below may break a table, a wire
shape or a refresh-token value. Each break is listed in §7 for the CHANGELOG, and no upgrade path is proposed.

**Out of scope:** auth events and the role-change audit (spec D9 — #153, #42); account and tenant deletion (spec §10);
an admin-dashboard screen for end users (spec §10); email + password and other providers (spec §5.8); the refresh
cookie (spec D7); anything in #542.

**Order of the sections.** #543 comes first, because its lock rule is what the others build on. Then #546, #544 and
#527. §6 lists the engine differences in one place, §7 the schema changes, §8 the blast radius, §9 the maintainer's
questions and §10 the slices.

---

## 2. #543 — the owner-removal × ban lock order, and a banned sole owner

### 2.1 Current state

**The multi-row writes over one person's credentials.** Four transactions sweep sets of rows in
`{prefix}_tenant_invitations` and `{prefix}_api_keys`, none of them ordered:

| Transaction | Rows it locks, in statement order | Where |
|---|---|---|
| Ban (`SetDisabledAsync(…, true)`) | account `X` → every live session of `X` → every live invitation `created_by = X` → every live token `minted_by = X` | `EfCoreAccountStore.Sessions.cs:174` (`DisabledAsync`); SQL `AuthTables.cs:259`, `InvitationTables.cs:110`, `AppTokenTables.cs:154` |
| Refresh-token reuse | session `S` → invitations `created_by = X AND created_at >= S.created_at` → tokens likewise | `EfCoreAccountStore.Sessions.cs:123` (`PresentedAgainAsync`), `:144` (`RevokeMintedSinceAsync`); SQL `InvitationTables.cs:117`, `AppTokenTables.cs:162` |
| Remove / leave (`RemoveMemberAsync`) | [actor's session] → tenant `T` (version bump) → member row `(T, X)` → invitations `(T, created_by X)` if `X` owned → tokens `(T, X)` | `EfCoreTenantStore.Members.cs:237` (`MemberWriteAsync`), `:211` (`RemoveAsync`); SQL `InvitationTables.cs:102`, `AppTokenTables.cs:139` |
| Demote (`UpdateMemberAsync`, `isOwner: false`) | [actor's session] → tenant `T` → member row → invitations `(T, X)` → service tokens `(T, minted_by X)` | `EfCoreTenantStore.Members.cs:176` (`WriteUpdateAsync`); SQL `AppTokenTables.cs:147` |

The *tables* are visited in one order: invitations before tokens. `EfCoreTenantStore.Members.cs:207-210` records that
order as the reason no 40P01 can happen. That holds between tables, but not between rows of one table. A bare
PostgreSQL `UPDATE … WHERE …` locks rows in the order its plan reaches them. The ban's
`created_by = @user_id` has no index (`InvitationTables.cs:52` indexes only `tenant_id`), so it is a sequential scan in
heap order. The removal's `tenant_id = @tenant_id AND created_by = @user_id` uses the `tenant_id` index once the table
is large enough, and so visits rows in index order. An index entry points at a HOT chain's *root* line pointer, while a
sequential scan meets the chain's *live* tuple at its own offset. After one HOT update — `uses = uses + 1` on an
accept, a `revoked_at` stamp — two rows of one person can therefore be visited A→B by one statement and B→A by the
other. With both transactions holding their first row, that is the cycle PostgreSQL breaks with `40P01` after
`deadlock_timeout` (1 s by default). One of the two then rolls back as an unhandled 500. This is **reasoned, not
reproduced**, exactly as the issue says, and §2.5 specifies the fact that decides it.

`_api_keys` is less exposed. Both of its sweep predicates have an index (`minted_by`, `tenant_id`;
`AppTokenTables.cs:65-66`), so two index scans meet shared rows in root-TID order. A sequential plan on either side
(a small table, stale statistics) reopens it, and `last_used_at` (`AppTokenTables.cs:93`, written once a minute per
token) is a steady source of HOT updates.

**A banned sole owner.** A ban revokes sessions, invitations and tokens but leaves memberships (`DisabledAsync`; S4
D16, S5 D10). A tenant whose only owner is banned has nobody who can rename it, invite, mint a service token or change
a member. No route grants ownership from outside: Management reaches end-user accounts only through
`PUT {m}/projects/{project}/accounts/{account}/disabled` (`ManagementEndpoints.Accounts.cs:51`). Two further facts
make this wider than the issue states:

- **The last-owner check counts a banned owner.** `TenantTables.cs:211` (`OwnersSql`) is
  `COUNT(*) … WHERE is_owner = 1`. With owners Ada (banned) and Bob, Bob may leave (`IsLastOwnerAsync`,
  `EfCoreTenantStore.Members.cs:169`, sees two owners). That orphans the tenant without any ban racing anything.
- **Nobody learns about it.** The ban answers 204 (`ManagementEndpoints.Accounts.cs:74`, `SetAccountDisabledAsync`), so the
  operator is not told that the account they just banned was the only owner of three tenants.

### 2.2 Threat model

| Who | What they gain |
|---|---|
| Two operators/owners acting at once (no attacker) | A 500 on the ban or on the removal; it rolls back and succeeds on retry. Liveness, not safety: no half-applied sweep commits. |
| A member who can trigger removals at will (an owner removing a member the operator is banning) | At most the same 500. Repeating it does not escalate anything. |
| A malicious sole owner who gets banned | Nothing new, but the tenant's members lose every management capability until an operator enables the abuser again — which also revives the abuser's ownership. The operator's only lever today is to un-ban the person they banned. |
| A co-owner leaving next to a banned owner | Strands the tenant, with no attacker needed (`OwnersSql`). |

### 2.3 The lock order — options

| | Option | For | Against |
|---|---|---|---|
| A | **One person, one mutex.** Every transaction that sweeps a person's credentials first takes that person's `_auth_accounts` row with the no-op `UPDATE` S3 D3 already uses (`TenantTables.cs:99`, `LockAccountSql`) | One SQL text on both engines (S3 D3's idiom); makes *all* sweeps of one person mutually exclusive, so the row order inside a table stops mattering; no new primitive | One more row lock per member write; needs a global table order so the new lock cannot invert with the tenant lock (below) |
| B | **Ordered locking per statement**: `UPDATE t … WHERE pk IN (SELECT pk FROM t WHERE … ORDER BY pk FOR UPDATE)` | The PostgreSQL-documented cure for multi-row deadlocks | SQLite has no `FOR UPDATE`. It needs a per-engine statement, and *per-engine SQL is a port member, never an `if` in the core* (memory *Engine scope*) — a dialect member for a problem SQLite cannot have. PostgreSQL's own docs warn that `ORDER BY` with a locking clause under `READ COMMITTED` can return rows out of order after a concurrent update, so it is subtler than it looks |
| C | **Retry on `40P01`** (a bounded retry of the whole store call) | Cheap; PostgreSQL expects applications to retry deadlock and serialization failures | Treats the symptom; every call site needs idempotent retry semantics, and a deadlock still costs `deadlock_timeout` (1 s) of latency on a security write |
| D | `SERIALIZABLE` for the sweeps | — | Not engine-agnostic in behaviour; turns deadlocks into serialization failures that still need C |

**Recommendation: A, with one total order written down.** The order covers every accounts and tenancy transaction:

```
_auth_accounts (at most ONE account row per transaction)
  → _auth_sessions
  → _tenants (version bump)
  → _tenant_members
  → _tenant_invitations
  → _api_keys
```

What changes, so that every path follows it:

| Path | Today | After |
|---|---|---|
| Ban, enable | account → sessions → invitations → tokens | unchanged |
| Reuse | session → invitations → tokens | **account `X`** → session → invitations → tokens. The token read stays a plain `SELECT`; only the reuse branch takes the lock, before it revokes |
| Mint (`EfCoreAppTokenStore.cs:163,169,195`) | account → session → tenant | unchanged |
| Create an invitation (`EfCoreTenantStore.Invitations.cs:43,127`) | session → tenant | **account (creator)** → session → tenant |
| Remove / leave / demote | [actor session] → tenant → member → sweeps | **account (the member whose credentials are swept)** → [actor session] → tenant → … A demotion that sweeps nothing (a role-only `PATCH`) takes no account lock |
| Accept (`InvitationAcceptance.cs:100`) | account (acceptor) → invitation → member insert | unchanged (it already starts at the account) |
| Sign-in, refresh rotate | account → session …; refresh row → session → personal tenant | unchanged |

**Why the lock is a no-op `UPDATE`** (`… SET last_sign_in_at = last_sign_in_at`, as `LockAccountSql` already is): it
is S3 D3's idiom, one SQL text for both engines — on SQLite it is a harmless write under the write gate. On PostgreSQL
it writes a new row version on every sweep; `SELECT … FOR NO KEY UPDATE` is the bloat-free alternative, not taken
because SQLite has no locking clause and the idiom's whole point is SQLite parity. Recorded so a later reader can
weigh the trade, not rediscover it.

"At most one account row per transaction" is the load-bearing clause. The removal locks the *member's* account, never
the acting owner's; the actor's own liveness is already the session lock. Any future write that needs two accounts
must take them in ascending `id` order and say so.

**Why A, beyond SQL portability:** it serialises a person's sweeps *against each other*. The ban, a removal, a
demotion and a reuse of the same person then never run concurrently, whatever plan each statement gets. B only orders
rows inside one statement. And C is what PostgreSQL recommends *in addition to* a consistent order, not instead of it.
A bounded retry of C (one retry on `40P01`/`40001`, logged) is worth adding as defence in depth only for the
background sweeper (§5), whose writes are idempotent. The request path keeps failing loudly, so a regression stays
visible.

### 2.4 A banned sole owner — options

| | Option | Verdict |
|---|---|---|
| A | **An operator route that makes an account an owner of a shared tenant** — joining it if needed — in Management | **Recommended.** It is the analysis' *Admin operácie* ("ban, force logout … s auditom") and GitHub's precedent: an enterprise owner can make themselves owner of an organization the enterprise owns, which is how GitHub recovers an organization with no usable owner |
| B | A documented SQL procedure | Refused: an operator needs database access, nothing is idempotent or audited, and it contradicts agent-first (§0) |
| C | Promote the longest-standing member automatically on a sole owner's ban | Refused: it grants power nobody chose, and the "best" successor is a judgment |
| D | Refuse to ban a sole owner unless the request names a successor | Refused as the default: a ban is an emergency action and must never be blocked. As an *optional* field of the ban it duplicates A |
| E | Do not count a banned owner as an owner | Necessary, not sufficient: it fixes the "co-owner leaves" path (§2.1) but recovers nobody |

**Recommendation: A + E + the ban names what it orphaned.**

1. **`PUT {m}/projects/{project}/tenants/{tenant}/owners/{account}`**, body `{ "roles": [ … ] }` (optional, each in
   the applied `assignableRoles`). It is idempotent: an existing owner answers 200 with the membership unchanged
   unless `roles` is sent. It runs at the `admin` level under the existing `ManagementOperation.ManageAccounts`
   (`ManagementEndpoints.Accounts.cs:66`), and is mapped only where `IAlvoAccountAdministration` is registered (the
   existing route's precedent). It answers:
   - 200 with the membership (`MembershipResponse`'s shape);
   - 404 for an unknown tenant or account, one named 404 each — Management is operator-only, so naming which one is
     missing is not an oracle;
   - 409 `conflict` for a personal tenant ("a personal tenant has exactly one member", S3);
   - 409 `conflict` for a disabled target account, with the fix "enable the account first, or choose another";
   - 422 for a role outside `assignableRoles`.

   Lock order (§2.3): target account (`LockAccountSql`, which also refuses a disabled one) → tenant bump → member
   upsert. It revokes nothing. With #546 it also writes `owner_granted_session = NULL` and the new
   `owner_granted_by_operator = 1` (§3.4) so the provenance is honest; **as built** (#546 deferred, §9.2) it records
   only `invitation_id = NULL`, as for a creator. It is logged like `AccountChanged`: actor, tenant id, account id, ids only.
   Roles matter because nobody changes their own membership (S3 D7). An operator-made owner with no roles could
   otherwise never hold one in that tenant.
2. **`OwnersSql` counts only owners whose account is not disabled** (`HonouredAccount`'s predicate, `TenantTables.cs:150`
   precedent). A co-owner can then no longer leave a banned owner alone. A banned owner still keeps the flag, so an
   enable restores exactly what was there.
3. **The ban answers 200 `{ "orphanedTenants": [ { "id", "name" } ] }`** — the shared tenants where, after the ban, no
   owner has an enabled account — instead of 204. The operator learns the consequence in the same call, and the agent
   gets the follow-up it needs to call (1). **As built** it is read right after the ban commits, not in its
   transaction. **Breaking** (204 → 200); listed in §7.

**A recorded window** (#543 review; ruled acceptable on the maintainer's behalf, §9.1). A ban of Ada racing her co-owner
Bob *leaving* can leave the tenant with no enabled owner while the ban answers `orphanedTenants: []`. Bob's leave takes
his own account row and the tenant; on PostgreSQL's `READ COMMITTED` its last-owner count still sees Ada enabled while
her ban is uncommitted, so it passes. If the ban's orphan read then runs before Bob's leave commits, it still sees
Bob. The tenant is recoverable with (1); the cost is an orphan the operator notices later rather than in the ban's
answer. Closing it would need the leave to lock every co-owner's account row — more than one account row per
transaction, against §2.3's rule. Operator acts may orphan; a member's acts alone never do.

**Tenants and accounts are host-wide.** One registry and one account table serve the deployment; the `{project}` in
the path is the Management surface's gate, not a partition. So a project `admin`'s grant — like their ban — acts on
host-wide rows, exactly as the existing ban route already did.

This does not reopen F5 D4 ("Management does not enumerate tenants"). The ban names only tenants that this account's
ban orphaned, and the operator already holds the account id.

### 2.5 Tests and acceptance criteria — the race fact first

Write the fact **before** the fix, run it on `main`, and record the result in the slice's PR. That fact is what decides
whether the deadlock is real.

**`PostgreSqlTenantStoreTests.An_owner_s_removal_racing_their_ban_never_deadlocks`** (new, PostgreSQL only). It is
staged so the inversion §2.1 reasons about is *constructed* rather than hoped for:

1. Seed production-shaped data: about 2 000 invitations by other creators across 200 tenants, then `ANALYZE`, so the
   removal's `tenant_id` predicate plans an index scan while the ban's `created_by` predicate stays a sequential scan.
   Assert both plans with `EXPLAIN (FORMAT JSON)` in the fact, so a planner change cannot silently make the fact
   vacuous. Then create Ada (owner of `T`, not sole) and two live invitations of hers in `T`, `A` then `B`.
2. HOT-update `A` (`UPDATE … SET uses = uses WHERE id = A`, committed). The heap now holds `B` before `A`'s live tuple;
   the index still reaches `A` first. Assert the order with `SELECT ctid`.
3. A holder transaction takes `SELECT … FROM invitations WHERE id IN (A, B) FOR UPDATE` (a lock that writes no new
   tuple version, unlike the `UPDATE`-based holders `PostgreSqlAccountStoreTests.cs:143` uses).
4. Start `RemoveMemberAsync(T, Ada, by Bob)` and `SetDisabledAsync(Ada, true)`. Wait until `pg_locks` shows **two**
   waiters (the existing `WaitingOnALockAsync` shape, generalised to a count).
5. Commit the holder, which releases `A` and `B` at once. Today the removal takes `A` and the ban takes `B`, then each
   waits on the other.
6. Assert that neither call throws `PostgresException { SqlState: "40P01" }`; that Ada is disabled and no longer a
   member of `T`; that every invitation and token of hers is revoked; and that Bob is still an owner.

Run it 10 times. **Red on `main` is the expected outcome.** If it is green, the PR records that and the fix still lands,
for the reason in Q1 (§9). **Measured on `main` (`6a71769f`), 2026-11-21: 9 of 10 iterations deadlocked (`40P01`)** —
the race is real, not only reasoned. Two more facts sit beside it:

- **`…Every_sweep_of_one_person_takes_their_account_row_first`** — deterministic, and the fact the fix is judged by.
  Hold Ada's account row (`LockAccountSql` in a holder). Start each of: the removal of Ada, the demotion of Ada, a
  reuse of Ada's session, the creation of an invitation by Ada. For each, wait until it blocks, then from a third
  connection run `SELECT … FROM invitations WHERE created_by = Ada FOR UPDATE NOWAIT` and the same over `_api_keys`.
  Both must succeed, which proves the waiting call holds no credential row while it waits for the account. Red on
  `main` for the removal, the demotion and the reuse (they take tenant or session rows first and reach the sweeps
  without the account).
- **`…A_ban_a_removal_and_a_reuse_of_one_person_at_once_leave_one_consistent_state`** — 20 iterations, three calls
  behind a `Barrier`, both engines (`TenantStoreFacts`; on SQLite it proves only the end state). The end state holds
  whatever order they ran in.

Recovery:

- `TenantStoreFacts.An_operator_makes_an_account_owner_of_a_tenant_whose_only_owner_is_banned` — both engines. Ban
  Ada, grant Bob through the store; Bob then renames, invites and removes Ada, and Ada's enable restores her as a plain
  member, since Bob removed her.
- `…An_operator_cannot_make_a_disabled_account_or_a_personal_tenant_s_stranger_an_owner`.
- `…A_co_owner_cannot_leave_a_banned_owner_alone` — `LastOwner`, red on `main`.
- `…The_ban_names_the_tenants_it_orphaned` — two tenants where Ada is sole owner, one where Bob co-owns, one where
  Bob's account is banned too, which counts as orphaned.
- `ManagementAccountsTests.Granting_ownership_*` — the HTTP answers above, the gate (a non-`admin` key gets 403), and
  the route absent without accounts.
- `SecretsNeverLoggedTests` stays green; the new log line holds ids only.

**Acceptance criteria.** (1) Zero `40P01` across the race fact's 10 runs on PostgreSQL. (2) The deterministic lock-order
fact is green for all four paths. (3) An operator recovers a banned sole owner's tenant in one call, without enabling
the banned account. (4) No sequence of member writes leaves a shared tenant without an enabled owner, unless the ban of
its last enabled owner is what did it — and then the ban names the tenant.

### 2.6 Deviations

- **Beyond spec §5.6**, which lists only disable and enable: a third account operation, granting ownership. The spec
  never addressed a sole owner, and the analysis §2.2 lists admin operations without bounding them.
- **The ban's answer changes from 204 to 200 with a body** — a deliberate break (§7).
- **Spec §6.4 ("the last owner … cannot leave")** now reads "the last *enabled* owner". That is stricter, never looser.

---

## 3. #546 — undo the ownership a stolen session already granted

### 3.1 Current state

A refresh-token reuse after the grace window revokes the session and, in the same transaction, every invitation and
app token the account created **since the session started** (`EfCoreAccountStore.Sessions.cs:123-157`; stack review
A-I1, `accounts.md` *The stack review's fixes*). A mint or an invitation still in flight re-reads its session under
lock (`AuthTables.cs:252`, `LockLiveSessionSql`) and either dies with it or is swept by it.

The sweep is **by time**, because nothing records which session minted what (`accounts.md:1350`). It therefore also
revokes what the person's *other* sessions created after the stolen one started. That collateral was accepted.

**What it does not undo** (`accounts.md:1370-1373`):

1. A membership created by an accomplice accepting an owner-granting invitation the thief created. The invitation is
   revoked, but the membership is a row of `_tenant_members` with `invitation_id` set (`TenantTables.cs:90`), and nobody
   touches it.
2. A promotion (`PATCH …/members/{id}` `isOwner: true`) or a role change the stolen session made.
3. A demotion or removal of a legitimate co-owner by the stolen session.
4. Anything the accomplice, now an owner, did next: their own owner-granting invitations, their service tokens, their
   promotions.

The thief controls the timing. It holds the refresh token it spent, so it can grant first and trigger the reuse later,
or never trigger it at all.

### 3.2 Threat model

| Attacker | Precondition | Gains today | After this design |
|---|---|---|---|
| Refresh-token thief (XSS, a malicious extension, a stolen device backup — spec D7 keeps the token in the body) | One refresh token of an owner of tenant `T` | Co-ownership of `T` for an accomplice: a durable seat inside the tenant wall, with every tenant role, invitations and service tokens, surviving the theft's detection | Owner-granting writes need a session authenticated within `StepUpMinutes` (§3.5). A token stolen later than that grants nothing. One stolen inside the window is undone on reuse: the accomplice's membership is removed and the closure of what it granted is undone (§3.6) |
| The same thief, demoting the legitimate co-owners | As above | Locks the owners out of their own tenant | Demoting or removing an owner also needs step-up. What a fresh session did is not reverted, because that needs the audit trail D9 defers (residual, §3.8) |
| A thief who never triggers reuse | As above | Everything above, until the session's idle limit (30 days) | The step-up still bounds it. Reuse is the only theft signal Alvo has (RFC 9700 §4.14.2 "cannot determine which party submitted"), so without it there is nothing to undo |

The out-of-model case is an attacker who runs code inside the victim's live app. It can act as the victim directly,
step-up included. No server-side mechanism distinguishes that from the person.

### 3.3 Prior art

- **RFC 9700 §4.14.2**: on reuse the AS "will revoke the active refresh token", and §4.14.2 also allows automatic
  revocation "in case of a security event". It says nothing about what the token's holder *did*. Undoing actions is
  beyond the BCP, as A-I1 already was.
- **RFC 7009 §2.1**: revoking a refresh token SHOULD invalidate the access tokens of the same grant. That is the
  cascade Alvo already does through the per-request session read (spec D5).
- **RFC 9470 (OAuth 2.0 Step Up Authentication Challenge)**: a resource server answers `401` with
  `WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age=…` when the authentication behind the
  token is too old for the operation, and the client re-authenticates.
- **NIST SP 800-63B-4 §5.2** (reauthentication: an overall timeout from the *authentication*, not from the last
  refresh; AAL2's overall limit SHOULD be ≤ 24 h, §2.2.3) and GitHub's *sudo mode* (sensitive actions re-prompt after a
  short window). Both support binding sensitive actions to the age of the authentication event.
- **Auth0** revokes the whole refresh-token family on reuse and logs it (`ferrt`). **Supabase** marks every refresh
  token of the session revoked. **Firebase** `revokeRefreshTokens` stamps `tokensValidAfterTime`. None of the three
  undoes downstream grants; all three leave notification to the application or the log.

### 3.4 Foundation — record which session did it

**Add a session column to every row a session can create or empower:**

| Table | New column | Written by |
|---|---|---|
| `_api_keys` | `session_id TEXT NULL` | a mint (`MintTokenRequest.Session`, already carried) |
| `_tenant_invitations` | `session_id TEXT NULL` | an invitation's creation (`CreateInvitationRequest.Session`, already carried) |
| `_tenant_members` | `joined_session TEXT NULL` | an accept: the **invitation's** `session_id`, copied, so it survives the invitation row being swept (§5) |
| `_tenant_members` | `owner_granted_session TEXT NULL`, `owner_granted_by_operator INTEGER NOT NULL DEFAULT 0` | an accept of a `grants_owner` invitation (its `session_id`); a `PATCH isOwner: true` (the actor's session); the operator route (§2.4, flag 1) |

`NULL` means "no Alvo session": a host without the person resolver, or `PersonSessionKey.SessionOf` returning `null`.

**The reuse sweep then becomes exact.** `RevokeCreatedSinceSql` and `RevokeMintedSinceSql` become
`… WHERE session_id = @session_id AND revoked_at IS NULL`, and `TokenRow.SessionStartedAt` goes. The other sessions'
credentials stop being collateral. The A-I1 fact
`AccountStoreFacts.A_reuse_revokes_the_tokens_and_invitations_created_since_its_session_started` is rewritten to
`…created_by_its_session`, and gains a counter-fact: a token minted after the stolen session started, but from another
session, **survives**. That reverses a documented trade (`accounts.md:1350-1351`), so it is recorded as a revision of
A-I1, not slipped in.

### 3.5 Prevention — step-up for writes that change who owns a tenant

**Which writes need a recent authentication:**

- creating an invitation with `isOwner: true`;
- `PATCH …/members/{id}` setting `isOwner: true`, or `isOwner: false` on an owner;
- `DELETE …/members/{id}` of an owner (not leaving yourself, which only reduces your own power).

Minting a service token is *not* gated. It carries no ownership (spec §16 B4), and §3.4's exact sweep revokes it on
reuse.

**"Recent" is the session's `created_at`** — the instant of the ID-token exchange that started it. A refresh never
moves it (spec §5.5). The check runs **inside the store transaction**, in the session re-read that already exists:
`LockLiveSessionSql` gains `AND created_at >= @authenticated_after` on these writes only. A zero-row answer is then told
apart by one more read, as `SessionEnded` (dead) versus the new `StepUpRequired` (live but old). The grant and the check
are therefore atomic, and the per-request read count (spec §14 AC2) does not change. The access token gains no claim.

**The answer follows RFC 9470:** `401`, `WWW-Authenticate: Bearer error="insufficient_user_authentication",
error_description="…", max_age=600`, and a problem document (S2 D4's convention) of the new type
`reauthentication-required` with `maxAge` (seconds) and a `detail` saying: sign in again with the provider, then repeat
the request. The client runs the token exchange again, which starts a new session, and retries with that session's
access token.

**Configuration:** `Alvo:Accounts:StepUpMinutes`, default **10**, range 1–1440 (NIST's AAL2 overall-timeout ceiling).
It is host configuration, not descriptor: the descriptor says *what* an app allows, and this is how strictly the host
proves who asked (*Descriptor ≠ infra config*, `alvo-architecture-rules`).

**What a fresh exchange proves.** It proves possession of the person's Google session at that moment, not that a human
typed a password. Google may issue an ID token silently. That is still exactly what a refresh-token thief lacks.

### 3.6 Cure — undo the ownership on reuse

Two transactions, because the cure touches other people's accounts and §2.3 allows one account per transaction:

**T1, the reuse transaction (as today, made exact).** It takes account `X` → session `S` (revoke, reason `reuse`) →
invitations `session_id = S` → tokens `session_id = S`. It also stamps the session row
`ownership_undo_pending = 1` (new column) when any `_tenant_members` row names `S` in `joined_session` or
`owner_granted_session` — one indexed `EXISTS`. T1 is the durable record that an undo is owed.

**T2…Tn, the undo (one member write per affected membership, in the normal lock order).** It runs right after T1, on
the refresh request, before the `invalid_grant` answer. It is re-run by the sweeper (§5) for any session still marked
pending, so a crash between T1 and T2 owes the undo, it does not lose it. For tenant `T`, the *tainted* set is computed
to a fixed point:

```
tainted₀ = { m ∈ members(T) : m.joined_session = S  ∨  m.owner_granted_session = S }
taintedₖ₊₁ = taintedₖ ∪ { m ∈ members(T) : m.joined_session ∈ sessions(taintedₖ)
                                          ∨ m.owner_granted_session ∈ sessions(taintedₖ) }
```

Here `sessions(A)` means the sessions of the accounts in `A`, restricted to grants made **after** each such account's
own taint instant (the `joined_at` or the owner grant). An accomplice's earlier, legitimate history is not undone. The
iteration is bounded by `members(T)` and capped at 100, with a warning beyond. For each tainted member, deepest first:

- **joined through a tainted invitation** → removed, through the existing `RemoveAsync` (`EfCoreTenantStore.Members.cs:211`).
  The invitations and tokens of that membership are swept with it, exactly as for any removal;
- **promoted by a tainted session** (`joined_session` untainted) → demoted, through the existing `WriteUpdateAsync`
  path. Their service tokens in `T` are swept, and their own pre-existing membership and roles stay.

Each is a member write with `Actor = system`, taking target account → tenant (§2.3). It obeys **one exception to the
last-owner rule**. The undo may leave a tenant without an enabled owner only when every remaining owner was tainted.
That tenant was created before the theft, so its legitimate owners were demoted by the stolen session (§3.2 row 2). The
undo then logs the tenant id as needing an operator (§2.4's route). The undo never invents an owner.

The person whose session was stolen is told nothing over HTTP. Their next refresh is the same `invalid_grant` as today,
and the removed accomplice's next request is the 401 a removed member gets.

### 3.7 Notification

Alvo sends no email, and auth events are deferred (spec D9: they need #153's tenant on the envelope and #42's audit).

**Recommendation: defer it, and leave a hook.** T1 already writes `revoked_reason = 'reuse'`. A Warning log line
`RefreshTokenReuse` is added with the account id, session id and the counts revoked, removed and demoted — ids and
counts only (spec §14 AC6). When D9 lands, the same point emits `alvo.auth.session.reuse_detected` to the account and
`alvo.tenant.member.removed` per undo. The issue's "notify other owners" is that second event.

### 3.8 Residuals, recorded rather than hidden

- A stolen session **younger than `StepUpMinutes`** can still demote or remove legitimate owners. Nothing reverts that
  without the audit trail of D9. §3.6 then leaves the tenant flagged for an operator.
- **Role changes** (not ownership) made by the stolen session stay. A role is inside the tenant and visible to every
  owner, and reverting it needs the same audit trail.
- A thief who **never triggers reuse** is bounded only by the step-up and the idle limit. Listing your own sessions and
  ending one (`GET/DELETE /auth/v1/user/sessions`) would let a person notice and act. That is a later slice, out of
  scope here.

### 3.9 Tests and acceptance criteria

- `AccountStoreFacts.A_reuse_revokes_only_what_its_session_created` — both engines. It replaces the "since" fact, adds
  the counter-fact (another session's later token survives), and is red on `main`.
- `…A_reuse_removes_a_member_who_joined_through_its_owner_invitation_and_what_they_granted` — accomplice B joins
  through `S`'s owner invitation, then invites C as owner from B's own fresh session, and C accepts. After the reuse,
  B and C are removed and their tokens and invitations revoked. An unrelated member D, invited by the victim's *other*
  session, stays. Both engines.
- `…A_reuse_demotes_a_member_its_session_promoted_and_keeps_their_membership`.
- `…An_undo_left_pending_by_a_crash_is_finished_by_the_sweeper` — T1 commits, T2 is skipped (a store seam that throws),
  then the sweeper completes the undo and clears the mark.
- `…The_undo_never_leaves_a_tenant_without_an_owner_it_did_not_taint` and
  `…A_tenant_whose_every_owner_was_tainted_is_logged_for_an_operator`.
- `PostgreSqlAccountStoreTests.An_owner_grant_racing_its_session_s_reuse_is_undone_or_never_happens` — staged with a
  held session lock, as the A-I1 race facts are.
- Step-up: `InvitationRoutesTests.An_owner_invitation_from_an_old_session_is_401_insufficient_user_authentication`
  (the header, `max_age`, the problem type, nothing created); `MemberRoutesTests.Promoting_demoting_or_removing_an_owner_needs_a_recent_sign_in`;
  `…Leaving_and_role_changes_need_no_step_up`; boundary facts at exactly `StepUpMinutes` (accepted) and 1 s past it
  (refused); `AccountStoreFacts.The_step_up_is_judged_inside_the_granting_transaction`.
- TeaPie `170-SelfService/012-reuse-ends-the-session` gains: Dana's stolen-and-reused session had created an owner
  invitation that Erin accepted, and after the reuse Erin's next request in Acme is 401. A new case
  `013-step-up` covers an owner invitation from a session older than the stack's `StepUpMinutes` (the demo stack sets
  it to 1, so the case waits it out as D8 does for the grace window).

**Acceptance criteria.** (1) After a reuse, no membership whose ownership traces to the reused session — directly or
through the closure — survives the reuse by more than one sweeper interval, even across a crash. (2) No owner-granting
write commits from a session authenticated more than `StepUpMinutes` ago. (3) The reuse revokes nothing created by
another session. (4) Spec §14 AC2 holds: a resolved Bearer request still costs one store read.

### 3.10 Deviations

- **Beyond RFC 9700 §4.14.2**: undoing grants. This continues A-I1's deviation.
- **RFC 9470 adopted, with the response body as a problem document** (spec D4's convention). Step-up is judged on the
  session's start, not an `auth_time` claim: Alvo is both the AS and the resource server, so the claim would only copy
  a column it reads anyway.
- **Revision of A-I1**: time-based → session-based attribution.
- **The undo's last-owner exception** (§3.6) — the one write allowed to break spec §6.4's invariant, and only in favour
  of a tenant's pre-theft state.

---

## 4. #544 — signing-key rotation, an emergency procedure, and Google's `azp`

### 4.1 Current state

- **One key per purpose, forever.** `AccountKeys.cs:42` (`Generation = 1`) creates the ES256 signing key and the
  refresh-MAC key on first use through `EnsureKeyAsync` (`EfCoreAccountStore.Keys.cs`). Its material is sealed with the
  secret cipher under `Alvo:Secrets:EncryptionKeyFile`. The table already has `generation`, `retires_at` and
  `UNIQUE (purpose, generation)` (`AuthTables.cs:120-131`), and `LiveKeysSql` reads unretired keys (`AuthTables.cs:285`).
  Nothing ever writes generation 2 or sets `retires_at`.
- **A replica holds its keys for its lifetime.** `KeySet.Signing` is fixed at the first load. A reload happens only for
  an unknown `kid`, at most once per 5 min (`AccountKeys.cs:40`, `ReloadInterval`), and keeps the old signing key
  (`AccountKeys.cs:141-146`). The refresh-MAC key is cached forever (`AccountKeys.cs:50`). A rotation written by another
  replica would never be signed with here.
- **The JWKS** publishes every unretired key and is cacheable for 300 s (`DiscoveryEndpoints.cs:49`).
- **No procedure** exists for a leaked or lost key file. The production guide says only that losing the file "breaks
  sign-in" (`website/src/content/docs/guides/production.mdx:186-188`).
- **`azp` is unchecked**, as a recorded deviation (`OidcIdTokenVerifier.cs:29-33`): every `aud` must be a configured
  client id (`:166`), and nothing else ties the token to *our* app.

### 4.2 Threat model

| Who | What they gain |
|---|---|
| Someone holding the signing key's private half (DB dump **and** key file; a backup; a memory dump) | Forged access tokens for any `sub` and `sid`. Since D5 every request reads the session, so a forgery works only with a **live** `sid` of that account. A `sid` is in every access token the person ever sent, so whoever saw one has it. Within that session they act as the person: read, write, mint tokens, create invitations |
| Someone holding the refresh-MAC key **and** a refresh-token row id (DB read) | Forged refresh values for that row: a session takeover |
| Someone who **lost** the key file | Not an attacker, but sign-in breaks. Every sealed row is unreadable, and today the first request throws on `Unprotect` |
| A Google Cloud service account (anyone with a GCP project) | An ID token whose `aud` the requester **freely chooses** (Google, *Token types*: for service-account ID tokens "the value can be freely chosen by the token requester"), signed by Google, `iss: https://accounts.google.com`, `email_verified: true`, `email: …@….iam.gserviceaccount.com`, and `azp` = the service account's numeric id. It passes `aud`-only verification. Under `signUp: open` that is an account per service account, scriptable at no cost. No impersonation: `sub` is the service account's own. No email-trust bypass: the email is neither `@gmail.com` nor `hd`-carrying, so §5.3 leaves it untrusted |

### 4.3 Prior art

- **RFC 7517** §4.5 (`kid` selects among keys) and §5 (a JWK Set holds several keys); **RFC 7518** §3.4 (ES256);
  **RFC 7638** (the thumbprint Alvo already uses as `kid`).
- **OIDC Core §10.1.1**: "The signer can begin using a new key at its discretion and signals the change … using the
  kid value … The JWK Set document … SHOULD retain recently decommissioned signing keys for a reasonable period of
  time." Verifiers refetch on an unknown `kid`. Alvo throttles that refetch (5 min) and lets clients cache the JWKS
  (300 s), so **publish before use** is needed to avoid refusals, not merely polite.
- **Supabase JWT signing keys**: *standby → current → previously used → revoked*. Wait for issued JWTs to expire
  before revoking the previous key, and allow ≥ 20 min for JWKS caches after creating a standby key.
- **OIDC Core §3.1.3.7** items 3–5 and §2's `azp`: "OPTIONAL. Authorized party — the party to which the ID Token was
  issued. If present, it MUST contain the OAuth 2.0 Client ID of this party." Item 5 says that when `azp` is present
  "the Client SHOULD verify that its client_id is the Claim Value." **Google's server-side checklist** names `aud` ("one
  of your app's client IDs"), `iss` and `exp`, and does not mention `azp`. Google's *Token types* defines a user ID
  token's `azp` as "the OAuth client that performed the OIDC authentication flow, identified by its OAuth client ID".
  The user flow's `azp` is therefore always one of *our* client ids, and a service account's never is.

### 4.4 Options and recommendation — scheduled rotation

| | Option | Verdict |
|---|---|---|
| A | **Instants in the rows, a background key refresher on every replica**: `activates_at` per key; the signing key is the newest generation with `activates_at ≤ now`; a replica re-reads the keys every 60 s off the request path | **Recommended** |
| B | Rotation only by operator call (Auth0's "rotate" button) | Fine for emergencies (§4.5), but a key that never rotates is the status quo the analysis calls non-standard ("key rotation cez JWKS", §2.2) |
| C | Reload on the request path when the cache is older than N | Violates the analysis' "< 1 ms overhead (cached JWKS)" and spec §14 AC2's "JWT validation does no I/O" |

**A, concretely:**

- `_auth_keys` gains **`activates_at TEXT NOT NULL`** (§7). Generation 1 is inserted with `activates_at = created_at`.
- **The lifecycle is all instants, so every replica agrees without coordination.** A key is *published* while
  `created_at ≤ now < retires_at` (or `retires_at` is null), *signing* while it is the highest generation with
  `activates_at ≤ now`, and *retired* after `retires_at`. A replica that loaded the next key before its activation
  switches at the exact instant, with no I/O, because the instant is in memory.
- **The refresher** is a `BackgroundService` in `MMLib.Alvo.Accounts` (the `LedgerSweeper` shape,
  `src/MMLib.Alvo/InboundWebhooks/Internal/LedgerSweeper.cs`). Every `KeyRefreshInterval` (60 s) it re-reads
  `LiveKeysSql` and swaps the `KeySet` atomically. It is the only reader besides the unknown-`kid` path, and that path
  stays as it is.
- **Scheduling the next key.** When the refresher sees that the signing generation `n` was activated at least
  `RotationDays − PublishLeadHours` ago, and generation `n + 1` does not exist, it inserts `n + 1` with
  `activates_at = now + PublishLead` through the existing `INSERT … ON CONFLICT (purpose, generation) DO NOTHING`. In
  the same transaction it sets generation `n`'s
  `retires_at = (n+1).activates_at + AccessTokenMinutes + 5 min` with `WHERE retires_at IS NULL`. Every replica tries;
  one wins; all converge (S2 Review Focus 2's idiom).
- **Defaults:** `RotationDays = 90` (Okta's quarterly precedent; Q6), `PublishLead = 24 h`. The lead must exceed the
  JWKS `max-age` (5 min), the unknown-`kid` throttle (5 min) and the refresher interval (1 min); a day leaves room for
  third-party verifiers that cache longer than they are told. The overlap after activation equals the longest
  access-token lifetime plus the 30 s validation skew, rounded up to 5 min.
- **Validation also checks `retires_at`** against the clock, from the cached set, so a retired key stops validating at
  its instant on every replica, not at the next refresh.
- **The refresh-MAC key is not rotated on a schedule.** It is symmetric and never leaves the server, and rotating it
  would need every live refresh value to carry a key generation. It rotates only in an emergency (§4.5), which kills
  every session, so no overlap is needed.

### 4.5 The emergency rotation

**`POST {m}/projects/{project}/accounts/signing-keys/rotate`**, body
`{ "reason": "compromise" | "lost-key-file", "revokeCredentialsCreatedSince"?: "<RFC 3339>" }`, at `admin` /
`ManageAccounts`. It runs in **one transaction**:

1. inserts signing generation `n + 1` with `activates_at = now`, and a new refresh-MAC generation;
2. sets `retires_at = now` on **every** older key of both purposes — an `UPDATE` that needs no decryption, so it also
   works when the old rows can no longer be unsealed (lost key file);
3. revokes **every live session** (`revoked_reason = 'key-rotation'`). With D5's per-request session read, every token
   signed by the old key is dead on its next request on every replica, *before* any replica's refresher has run;
4. if `revokeCredentialsCreatedSince` is given, revokes every app token and invitation created at or after it — the
   operator's estimate of when the compromise began. With the key, an attacker could act inside any live session they
   knew a `sid` of, and §3.4's attribution cannot single those sessions out.

It answers 200 `{ "kid": "<new>", "sessionsRevoked": n, "credentialsRevoked": m }` and logs ids and counts. Concurrent
calls converge on one generation through the unique key; a second call is a new rotation, which is what an operator
repeating it means.

**Why sessions die rather than only the key retiring.** A forged token needs a live `sid` (§4.2). Killing every session
makes every `sid` an attacker learned worthless. People sign in again, and that is the stated cost of a key compromise.

**The lost key file.** Today `AccountKeys.LoadAsync` throws on the unreadable generation 1 at the first request. After
this change the boot logs a **critical** line when the current signing row cannot be unsealed. The line names this
procedure: mount a new key file, call the emergency rotation (which never decrypts the old rows), and everyone signs in
again. Secrets in `ISecretStore` under the old file are a separate loss, already documented. The production guide gains
the runbook for both cases.

**When the key file *and* the database leaked**, do both: rotate the key file (a new file, re-enter the
`ISecretStore` secrets) and call the emergency rotation with `revokeCredentialsCreatedSince`.

### 4.6 `azp`

| | Option | Verdict |
|---|---|---|
| A | **When `azp` is present it must be one of the provider's `ClientIds`; when absent, `aud` must be single** | **Recommended.** OIDC Core §3.1.3.7 item 5 read as "its client_id" ∈ the configured set, since Alvo is one relying party with several clients (web, iOS, Android) |
| B | Require `azp` present | Refuses providers and flows that omit it; buys nothing over A for Google, whose user tokens always carry it |
| C | Refuse `…gserviceaccount.com` emails | A blocklist of one provider's naming convention; refused |
| D | Keep the deviation, document that `allowedEmailDomains` or `invitationOnly` closes it | It leaves `open` sign-up scriptable |

**A** retires the recorded deviation's premise rather than contradicting it. Its concern was a mobile sign-in whose
`azp` is the mobile client while `aud` names the web client. Under A that token is accepted *as long as the mobile
client id is listed*, and spec §11.2's example configuration already lists `…-ios…` beside `…-web…`. The
sign-in guide's Google set-up then says plainly: **list every client id your apps sign in with — web, iOS, Android —
not only the one the backend verifies against.** A new refusal value, `IdTokenRefusal.AuthorizedParty`, answers the
exchange's existing `invalid_request` (RFC 8693 §2.2.2) with no new problem type.

### 4.7 Tests and acceptance criteria

- `AccountKeysTests` (fake clock):
  - `The_next_key_is_published_a_day_before_it_signs`;
  - `A_replica_switches_to_the_next_key_at_its_activation_without_io`, which counts store reads;
  - `A_retired_key_stops_validating_at_its_instant`;
  - `Two_replicas_converge_on_one_next_generation` (both engines, `AccountStoreFacts`);
  - `The_previous_key_validates_until_every_token_it_signed_has_expired`;
  - `The_refresher_failing_keeps_the_keys_it_has`.
- `DiscoveryTests.The_jwks_publishes_the_next_key_before_it_signs_and_drops_a_retired_one`, checked with an
  independent handler.
- `PersonResolverTests.An_access_token_is_validated_without_reading_keys_again` stays green: the AC2 guard.
- Emergency: `AccountStoreFacts.An_emergency_rotation_retires_every_key_and_revokes_every_session_in_one_transaction`;
  `…It_retires_keys_it_cannot_unseal`, sealing generation 1 under another cipher;
  `…It_revokes_credentials_created_since_the_given_instant_only`; `ManagementAccountsTests.Rotating_keys_*` for the
  gate, the answer and the absent route without accounts; `AccountsEngineFacts.After_an_emergency_rotation_every_old_token_is_401_on_every_replica`
  with two hosts over one store.
- A boot whose signing row cannot be unsealed logs the critical line naming the procedure, and sign-in answers a 503
  `sign-in-unavailable` rather than a 500. Open as **Q7**: a new problem slug.
- `azp`: `OidcIdTokenVerifierTests.An_id_token_whose_azp_is_not_a_configured_client_is_refused` (red on `main`), with
  the shape of a service-account token: free `aud` = our client, numeric `azp`;
  `…A_mobile_token_whose_azp_is_a_listed_mobile_client_is_accepted`;
  `…A_token_without_azp_and_several_audiences_is_refused`.
- TeaPie `170-SelfService/001-sign-in` keeps passing: the suite's `TestIssuer.csx` must set `azp` to the configured
  client id. That is one line, and the case gains *"an ID token whose azp names another client is invalid_request"*.

**Acceptance criteria.** (1) A scheduled rotation causes zero 401s for live sessions, on every replica and for a JWKS
consumer honouring `max-age`. (2) The emergency rotation makes every pre-rotation access token fail on its next
request, everywhere, in one call. (3) No validation does I/O (spec §14 AC2; the analysis' < 1 ms). (4) A
service-account ID token never signs anyone in.

### 4.8 Deviations

- **The refresh-MAC key rotates only in an emergency.** Supabase and OIDC §10.1.1 speak to the signing key; nothing
  publishes this key.
- **`azp` is checked against the set of configured client ids**, which is wider than item 5's single `client_id`. Alvo
  is one relying party fronting several OAuth clients. The S2 deviation (`azp` unchecked) is **withdrawn**.
- **The emergency rotation revokes every session.** OIDC §10.1.1 asks only for retiring the key. Alvo's tokens carry a
  `sid` the server checks, so killing sessions is what makes a stolen key worthless at once.

---

## 5. #527 — sweep expired sessions, refresh tokens, app tokens and invitations

### 5.1 Current state

Nothing deletes a row from `_auth_sessions`, `_auth_refresh_tokens`, `_api_keys` or `_tenant_invitations` (spec D10;
`accounts.md:257-260`, `:1099-1101`). A session refreshed every 15 minutes adds about 96 refresh rows a day
(spec D10). `GET /auth/v1/tokens` returns every token the person ever minted or can see, revoked and expired ones
included, in one unpaged array (`TokenRoutes.cs:69`; `AppTokenTables.cs:115`), and `GET …/invitations` likewise
(`InvitationTables.cs:82`). Indexes: sessions on `account_id`, refresh tokens on `session_id` (`AuthTables.cs:56-58`).
Nothing indexes an instant.

**The constraint D10 names.** A session row's `subject_token_id` is what makes an ID token single-use. A session must
not be deleted while its ID token could still be accepted: `iat` ≤ 5 min, plus 2 min skew.

**A constraint the issue's third bullet misses.** The issue asks to delete "refresh-token rows rotated longer ago than
the grace window". Reuse detection works by *recognising a rotated token* (RFC 9700 §4.14.2). Alvo's refresh path
reads the presented id's row (`AuthTables.cs:214`, `RefreshTokenSql`). A deleted ancestor would answer "unknown" →
`invalid_grant` **without revoking the session**. Take a thief who rotated the victim's tip: the victim's next refresh
presents the now-rotated token. If that row is gone, the victim is refused, and the thief's session lives on. That
breaks `Reuse_inside_the_window_returns_the_same_child_and_after_it_kills_the_session` for any reuse later than the
retention, and the analysis' acceptance criterion "použitie rotovaného tokenu invaliduje celú family do 1 s" (§2.2).

### 5.2 Options — refresh-token ancestors

| | Option | For | Against |
|---|---|---|---|
| A | Keep every ancestor until its session is swept | No refresh-path change | Per active session, up to lifetime ÷ access-token lifetime rows: 180 d × 96 = ~17 000. Bounded, but large, and it is the growth #527 exists to stop |
| B | **Bind the session into the value**: `alvo_rt_<session>_<id>_<mac>`, MAC over `session ‖ id`. A MAC-valid value whose id has no row but whose session is live is a reuse. Then every ancestor rotated more than the grace window ago can be deleted | Reuse detection no longer needs ancestor rows. A live session keeps at most two rows: the tip, and its parent while inside the grace window. Supabase's v2 refresh tokens moved the same way, to a per-session counter instead of a parent-child table | Refresh-token format change (§7). An unknown id becomes a reuse signal, so the MAC must be checked first, as it already is (`accounts.md:180`, "Check the MAC before any read") |
| C | Delete ancestors after the idle limit (30 d) | Simple | Loses the signal exactly in the scenario above, once the victim has been away longer than 30 days |

**Recommendation: B.** Every id Alvo ever issued is either in the table, swept, or rolled back before anyone saw it (a
child is minted inside the committing transaction). A MAC-valid value naming a live session but no row can therefore
only be a swept ancestor, and is answered exactly as a reuse after the window: the session is revoked, with §3's
sweeps. A value naming a dead or unknown session is the ordinary dead `invalid_grant`, revoking nothing, since there is
nothing left to revoke. `RefreshTokenFormat` grows to 8 + 32 + 1 + 32 + 1 + 43 = 117 characters, parsed by position
as today (`RefreshTokenFormat.cs:10-40`).

### 5.3 The sweep

**Where it lives: ONE internal framework sweeper, with pluggable, batched, per-table sweep jobs** (controller ruling
2026-11-21, §9.2). It is a single hosted `BackgroundService` in the core, following `LedgerSweeper`'s shape: hourly,
`PeriodicTimer` on the injected `TimeProvider`, failures logged and retried next tick, host stop ends it quietly. It
runs every registered **sweep job** — an internal contract naming one table, its predicate's thresholds and its batch
bound. Each job deletes at most `batch × maxBatches` rows a tick, in short transactions of its own, and one failing job
never stops the others. Everything is `internal`: jobs are registered with `TryAddEnumerable` by the package that owns
the table (Accounts registers the four of this section from `AddAlvoAccounts` through Abstractions' existing
`InternalsVisibleTo` grants), and no type joins the public API. `LedgerSweeper`'s two ledgers are its first two jobs, so
a host still runs one sweep timer, not two. **The Apps line's N6** (a row `retention` trait; pruning `alvo_outbox`, #154,
and `alvo_idempotency`, #115) **adds jobs to this sweeper**, never a second one. This is **a deviation from the issue's
"an Alvo scheduled job"**: Alvo's schedules are descriptor-declared and run per tenant as the system actor, while this
is infrastructure. Every replica runs it, and the deletes are idempotent.

**What it deletes.** Each threshold is computed in C# and bound as a parameter, so the SQL text is one for both
engines. `R` is the retention, default 30 days, configurable as `Alvo:Accounts:RetentionDays` (min 1; Q9).

| Table | Rows | Kept regardless |
|---|---|---|
| `_auth_sessions` | `revoked_at < now − R`, or `expires_at < now − R`, or `COALESCE(last_refreshed_at, created_at) < now − RefreshIdle − R` | `ownership_undo_pending = 1` (§3.6); `created_at ≥ now − 7 min` (the ID-token window — implied by `R ≥ 1 d`, asserted anyway) |
| `_auth_refresh_tokens` | every row whose session is being deleted in the same batch; and every row with `rotated_at < now − Grace` (option B) | the tip (`rotated_at IS NULL`) and a parent inside the window |
| `_api_keys` | `revoked_at < now − R` or `expires_at < now − R` | — |
| `_tenant_invitations` | `revoked_at < now − R`, or `expires_at < now − R`, or used up (`uses >= max_uses`) and `created_at < now − R` | — (`_tenant_members` copies the provenance it needs, §3.4) |

**Bounded.** Each statement is `DELETE FROM t WHERE pk IN (SELECT pk FROM t WHERE … LIMIT @batch)`, which SQLite and
PostgreSQL both accept (SQLite's own `DELETE … LIMIT` needs a compile option, so it is not used). `@batch` is 500, at
most 20 batches per table per tick, and each batch is its own short transaction. Sessions and their refresh tokens go
together in one transaction, children first. A tick then removes at most 10 000 rows per table per replica. A backlog
larger than that drains over hours, which is fine for a table nothing reads by scan.

**It takes no account lock.** It deletes only dead rows, which no sweep of §2.3 locks (they all filter
`revoked_at IS NULL`). Two replicas deleting overlapping batches can still deadlock each other on PostgreSQL, so the
sweeper retries a batch once on `40P01` and then leaves it to the next tick, logged at Debug. This is §2.3 C, used only
where the write is idempotent.

**It also finishes owed undos** (§3.6): sessions with `ownership_undo_pending = 1` older than one minute.

**New indexes** (plain `CREATE INDEX IF NOT EXISTS`, one text for both engines): `_auth_sessions(expires_at)`,
`_auth_sessions(revoked_at)`, `_auth_refresh_tokens(rotated_at)`, `_api_keys(expires_at)`, `_api_keys(revoked_at)`,
`_tenant_invitations(expires_at)`, and `_tenant_invitations(created_by)`. The last one also gives the ban's sweep an
index, and §2.1's reproduction must then account for it. The fact asserts its plans, so the change shows up as a
deliberate edit of the fact.

### 5.4 The unbounded lists

A sweep bounds the tables but not a person's view. At the `Create` budget (10 a minute), a person can mint and revoke
about 14 000 tokens a day, all listed for `R` days.

**Recommendation: page `GET /tokens` and `GET /tenants/{id}/invitations` with the Data API's own convention**
(`website/src/content/docs/data-api/conventions.mdx`): `limit` (1–200, default 50) and `after={cursor}`, answering
`{ "items": [...], "next": <opaque> | null }`. The cursor is keyset over the existing order `(created_at DESC, id)`,
opaque, and a forged one answers an empty page, as the Data API's does. It is **breaking** (a bare array becomes an
envelope). The alternative of keeping the array and capping it at 200 silently hides rows, which the Data API's
contract refuses to do. Q10.

### 5.5 Tests and acceptance criteria

- `AccountStoreFacts` (both engines, fake clock):
  - `The_sweep_deletes_only_sessions_dead_for_longer_than_the_retention` (each of revoked, expired, idle, one second
    each side);
  - `…Never_deletes_a_session_inside_the_id_token_window`;
  - `…Keeps_a_session_whose_ownership_undo_is_owed`;
  - `…Deletes_a_session_s_refresh_tokens_with_it`;
  - `…Deletes_rotated_ancestors_past_the_grace_window_and_keeps_the_tip`.
- `AccountStoreFacts.A_swept_ancestor_presented_again_kills_its_live_session` — red without option B. **The test that
  §5.1's constraint needs.** Its sibling: `…A_swept_ancestor_of_a_dead_session_is_the_ordinary_dead_answer`.
- `RefreshGrantTests.Every_dead_refresh_token_answers_byte_identically` gains the new shape.
  `RefreshTokenFormatTests` covers the 117-character value: canonical only, MAC over session and id, and a swapped
  session refused.
- `AppTokenStoreFacts.The_sweep_deletes_tokens_dead_for_longer_than_the_retention`;
  `InvitationStoreFacts.The_sweep_deletes_dead_invitations_and_members_keep_their_provenance`.
- `…Each_tick_deletes_at_most_its_bound` (seed 12 000 dead rows; one tick deletes 10 000).
  `PostgreSqlAccountStoreTests.Two_replicas_sweeping_at_once_never_fail_a_tick`.
- `FrameworkSweeperTests` (LedgerSweeper's shape, the ledgers' two jobs folded in): it runs hourly, a failure is logged and retried, stopping is quiet,
  and it is absent without `AddAlvoAccounts`.
- `TokenRoutesTests.The_list_pages_with_limit_and_after`, `…A_forged_cursor_is_an_empty_page`; the same for
  invitations; `AccountsOpenApiTests` snapshots move.
- TeaPie `170-SelfService/004` and `010` (they read `GET /tokens`), and `002` (`GET …/invitations`) read `items`.

**Acceptance criteria.** (1) Steady state: for a population whose sessions refresh every 15 minutes, the refresh-token
table holds at most 2 rows per live session plus the dead sessions' rows within `R`. (2) No reuse goes undetected
because of the sweep (the swept-ancestor fact). (3) No ID token becomes replayable because of the sweep. (4) A tick is
bounded per table per replica.

### 5.6 Deviations

- **A framework background sweeper, not "an Alvo scheduled job"** (the issue's wording) — `LedgerSweeper` precedent.
- **The refresh-token value binds its session** (option B) — beyond spec D6's `alvo_rt_<id>_<mac>`.
- **Revoked and expired app tokens and invitations disappear from the lists after `R`.** S5 D14's "204, also the second
  time" for a revoke holds within `R`; past it the id is the unknown 404. The same applies to S4's invitation revoke.

---

## 6. SQLite and PostgreSQL

| Concern | SQLite | PostgreSQL |
|---|---|---|
| Deadlocks, lock order (#543) | Impossible: the write gate (`SerialRelationalWriteGate`) plus `BEGIN IMMEDIATE` serialise every write. The facts there prove end states only | The engine where §2.3 matters (`NoRelationalWriteGate`). The race and lock-order facts run here only |
| The account-row mutex | A no-op under the gate | A real row lock, `READ COMMITTED` re-check |
| Step-up check in-transaction (#546) | Serialised | `LockLiveSessionSql`'s predicate is re-checked after any wait |
| The undo's fixed point (#546) | Iterated in C# — no recursive CTE needed, because each step is a member write in its own transaction | Same |
| Key convergence (#544) | Serialised | `ON CONFLICT DO NOTHING` + read-back, as today |
| Bounded deletes (#527) | `DELETE … WHERE pk IN (SELECT … LIMIT n)` (no `DELETE … LIMIT`) | Same text; two replicas can deadlock → retry once |
| Instants | Fixed-width `TEXT` (`StoredInstant`), compared as text | Same |
| Plans (#543's fact) | n/a | The fact asserts its plans with `EXPLAIN (FORMAT JSON)` |

One SQL text serves both engines throughout. No dialect port member is added.

## 7. Data, schema and wire changes — for the CHANGELOG

All are breaking DDL or wire changes, acceptable pre-v0.1, and none carries an upgrade path.

| Issue | Change |
|---|---|
| #543 | Management: **new** `PUT {m}/projects/{project}/tenants/{tenant}/owners/{account}`; the ban (`PUT …/accounts/{account}/disabled`, `disabled: true`) answers **200 `{orphanedTenants}`** instead of 204. Last-owner semantics: a banned owner no longer counts |
| #546 | `_api_keys.session_id`, `_tenant_invitations.session_id`, `_tenant_members.joined_session`, `.owner_granted_session`, `.owner_granted_by_operator`, `_auth_sessions.ownership_undo_pending`. New problem type `reauthentication-required` (401, RFC 9470 header). New option `Alvo:Accounts:StepUpMinutes`. The reuse sweep is by session, not by time |
| #544 | `_auth_keys.activates_at`. New options for signing-key rotation (`RotationDays`, `PublishLeadHours`). Management: **new** `POST {m}/projects/{project}/accounts/signing-keys/rotate`. Google sign-in refuses an `azp` that is not a configured client id — **apps must list their mobile client ids**. Possibly a `sign-in-unavailable` 503 (Q7) |
| #527 | Refresh-token value `alvo_rt_<session>_<id>_<mac>` (117 chars); every existing refresh token dies on upgrade. Seven new indexes. `GET /auth/v1/tokens` and `GET …/invitations` answer `{items, next}` with `limit`/`after`. Dead app tokens and invitations vanish after `RetentionDays`. New option `Alvo:Accounts:RetentionDays` |

`PublicApi.*.verified.txt` grows only by the new `AlvoAccountsOptions` members. Each is justified against *public is
the contract* (`alvo-architecture-rules`), and the stores and ports stay internal. The descriptor schema
(`schema/project.schema.json`) does not change.

## 8. Blast radius

- **`docs/architecture/accounts.md`:**
  - *Deviations* (`azp` withdrawn);
  - *Not in S2* (#544 and #527 done);
  - *The tables* (columns);
  - S3 *Invariants* (last enabled owner);
  - S4 *Not in S4* (#543);
  - S5 *The table*, *Not in S5*;
  - *The stack review's fixes*, revised: A-I1 becomes session-based, and the "Still open" paragraph is closed;
  - a new *Lock order* subsection holding §2.3's table, and a *Keys* subsection.
- **The accounts spec** (`2026-11-13-…`): D10 is answered; §5.6 gains the ownership grant; §6.4 says "enabled owner";
  §14 is unchanged.
- **Guides:**
  - `sign-in.mdx`: list every client id; the step-up answer and how a client handles it; the refresh value is opaque,
    so do not parse it;
  - `tenants-and-invitations.mdx`: owner changes need a recent sign-in; what a reuse undoes; the paged invitation list;
  - `tokens.mdx`: the paged list; dead tokens vanish after the retention;
  - `production.mdx`: key rotation, the emergency runbook, the lost-key-file runbook, `RetentionDays`,
    `StepUpMinutes`;
  - the generated `reference/accounts-api`.
- **TeaPie `170-SelfService`:** `001` (`azp` in `TestIssuer.csx`, plus a refusal case); `002`, `004` and `010` (paged
  lists); `012` (the undo); a new `013-step-up` (the demo stack sets `StepUpMinutes: 1`). `090-Docs` pins only `/auth/v1`
  paths and gains none. The Management routes are not in that document, so confirm this in slice 3.
- **Skills:** `plugins/alvo/skills/alvo-descriptor-accounts` — the descriptor does not change, but its prose on
  invitations and tokens names the list shape and owner invitations. It is product text: drift-tested and size-capped.
  `.claude/skills/alvo-security-core-review` gains one checklist line: "a write over a person's credentials takes their
  account row first (§2.3)".
- **Admin dashboard:** no end-user screen exists (spec §10), so nothing breaks. The two new Management operations are
  natural dashboard actions later, through `IAlvoManagement` if they are promoted from internal routes. Not in this
  design.
- **`problem-types.json`** and the OpenAPI snapshots (Accounts, Management): new types and paths.
- **Load:** `test/load/docker-compose.load.yml` resets the demo's sign-in, so load is unaffected. The sweeper runs on
  load hosts that add accounts only.

## 9. Open questions — the maintainer's decisions (ruled, §9.1)

Each has a recommendation; none blocks writing the race fact (slice 1).

1. **#543 — fix the lock order even if the race fact stays green on `main`?** *Recommend yes.* The deterministic
   lock-order fact shows the order is undefined by construction, the fix is the existing S3 D3 idiom, and a later index
   (§5.3 adds one) or a plan change would make it real without any code change.
2. **#543 — the ownership grant's Management operation and shape.** *Recommend* reusing `ManageAccounts` at `admin`,
   `PUT …/tenants/{tenant}/owners/{account}` with optional `roles`, and changing the ban to 200 `{orphanedTenants}`.
   The alternative is a new `ManageTenants` operation, if you expect tenant administration to grow (rename, delete) and
   want a separate grant.
3. **#546 — the undo's collateral.** It removes members who joined through an owner invitation the stolen session
   created, transitively (§3.6). *Recommend yes*: the closure is what makes the step-up's window the only residual,
   and the removed people re-join by a fresh invitation.
4. **#546 — step-up default and scope.** *Recommend* 10 minutes, gating owner-granting invitations and promoting,
   demoting or removing an owner. Not service-token mints.
5. **#546 — notification.** *Recommend* deferring to spec D9's auth events, with a Warning log line now.
6. **#544 — scheduled rotation by default?** *Recommend* yes, every 90 days with a 24 h publish lead. Alternative:
   manual only (`RotationDays: 0` disables it), Auth0's model.
7. **#544 — a lost key file.** Answer a new `503 sign-in-unavailable` problem (*recommend*), or keep today's 500 with
   the critical boot log only.
8. **#544 — `azp` policy.** *Recommend* "if present, must be a configured client id". It makes listing mobile client
   ids mandatory for mobile apps, which needs one sentence in the guide.
9. **#527 — retention.** *Recommend* 30 days, configurable (`RetentionDays`, min 1). The precedent, `LedgerSweeper`,
   uses constants, but token and invitation history is user-visible, so an operator may want it longer.
10. **#527 — paged lists.** *Recommend* the Data API envelope `{items, next}` with `limit`/`after` (breaking).
    Alternative: keep the bare array and hide dead rows older than 7 days (non-breaking, but still unbounded for live
    plus recent).
11. **#527 — the refresh-token format change (option B).** *Recommend* yes. It is the only option under which the
    sweep cannot weaken reuse detection; option A keeps the format but leaves ~17 000 rows per long-lived session.

### 9.1 Rulings

Ruled 2026-11-21 by the debt-cleanup session on the maintainer's behalf (the maintainer away, rulings delegated):
every recommendation above is accepted as written. One line each, with the cost if the ruling is wrong:

1. **Q1 — fix the lock order regardless of the race fact.** Cost if wrong: one extra no-op row lock per member write
   that sweeps.
2. **Q2 — reuse `ManageAccounts` at `admin`; the ban answers 200 `{orphanedTenants}`.** Cost if wrong: a later
   `ManageTenants` operation moves one route's gate; the ban's body is additive to change again. **Review addendum:**
   the ban × co-owner-leaving window of §2.4 (the ban can answer `orphanedTenants: []` for a tenant the race left with
   no enabled owner) is accepted — operator acts may orphan, and the grant recovers it. Cost if wrong: an orphan noticed
   later than the ban.
3. **Q3 — the undo removes members admitted through a stolen session's owner invitations, transitively.** Cost if
   wrong: a legitimate member re-joins by a fresh invitation.
4. **Q4 — step-up 10 minutes, for owner-granting invitations and promoting, demoting or removing an owner.** Cost if
   wrong: one configuration default or one gated route moves.
5. **Q5 — notification deferred to spec D9's auth events, with a Warning log line now.** Cost if wrong: nobody is told
   until D9 lands, as today.
6. **Q6 — scheduled rotation every 90 days with a 24 h publish lead; `RotationDays: 0` turns it off.** Cost if wrong:
   a default changes.
7. **Q7 — a lost key file answers `503 sign-in-unavailable`.** Cost if wrong: one problem slug to retire before v0.1.
8. **Q8 — `azp`, when present, must be a configured client id.** Cost if wrong: a mobile app that did not list its
   client id is refused until it does — one configuration line, and the guide says so.
9. **Q9 — retention 30 days, configurable, minimum 1.** Cost if wrong: a default changes.
10. **Q10 — paged `{items, next}` lists with `limit`/`after`, breaking.** Cost if wrong: two list shapes to revert
    before v0.1.
11. **Q11 — the session-bound refresh-token value.** Cost if wrong: every live refresh token dies once more on the
    revert; no users yet.

### 9.2 Scope ruling

**Controller ruling 2026-11-21 on the maintainer's behalf; why: budget + Apps line + no users; cost if wrong: rotation
and step-up/undo land later, pre-v0.1, no deployments.**

- Implement only **#543 → #544 `azp` → #527**, in that order (slices 1, 3; 2; 7, 8, 9).
- **#527's sweeper is one internal framework sweeper** — a hosted background service with pluggable, batched, per-table
  sweep jobs, internal, with no new public surface unless justified (§5.3). The Apps line's N6 (a row `retention`
  trait; pruning `alvo_outbox`, #154, and `alvo_idempotency`, #115) will add jobs to it.
- **#544's key rotation (slices 10, 11) and #546 (slices 4, 5, 6) are accepted but DEFERRED.** Their design stands as
  written; they are not built now.

## 10. Slices — ordered, each independently mergeable, each `needs-deep-review`

Each slice runs the `alvo-security-core-review` checklist plus reviewer passes standing in for `/security-review` and
`/code-review` (spec §8), then `alvo-plan-guard`, and its PR carries the label `needs-deep-review`. Estimates are
working days for one implementer, tests included.

| # | Slice | Issue | Depends on | Estimate |
|---|---|---|---|---|
| 1 | **The race fact, then the lock order.** First commit: §2.5's race fact and the deterministic lock-order fact, run on `main`, with the result written into the PR. Then the "one person, one mutex" order (§2.3), the order table in `accounts.md`, and the security-core checklist line | #543 | — | 1.5 |
| 2 | **`azp`**: the check, the refusal, `TestIssuer.csx`, the guide sentence, the S2 deviation withdrawn | #544 | — | 0.5 |
| 3 | **A banned sole owner**: `OwnersSql` counts enabled owners; the Management ownership grant; the ban answers `orphanedTenants` | #543 | 1 | 1.5 |
| 4 | **DEFERRED** — **Session attribution** (§3.4): the four columns, the exact reuse sweep, the rewritten A-I1 facts | #546 | 1 | 1 |
| 5 | **DEFERRED** — **Step-up** (§3.5): `StepUpMinutes`, the in-transaction check, RFC 9470 answer, `013-step-up` | #546 | 4 | 1.5 |
| 6 | **DEFERRED** — **The ownership undo** (§3.6): T1 marker, the fixed-point undo, the reuse log line, `012` grows | #546 | 4 (5 recommended first) | 2 |
| 7 | **Session-bound refresh values** (§5.2 B): the format, the swept-ancestor-is-reuse rule | #527 | — | 1 |
| 8 | **The sweeper** (§5.3): the one internal framework sweeper with per-table jobs (the ledgers folded in), the accounts' four jobs, the indexes, the bounded deletes | #527 | 7 | 2 |
| 9 | **Paged lists** (§5.4): `GET /tokens`, `GET …/invitations`, the TeaPie and guide changes | #527 | — | 1 |
| 10 | **DEFERRED** — **Key lifecycle** (§4.4): `activates_at`, the refresher, scheduled rotation, retirement in validation | #544 | — | 2 |
| 11 | **DEFERRED** — **Emergency rotation** (§4.5): the Management route, the lost-key-file boot line, the production runbook | #544 | 10 | 1.5 |

**Total ≈ 15 days; ≈ 7.5 days are in scope now** (§9.2). The numbers above name the slices; **the order they land in
was ruled by value and risk** (2026-11-21, on the maintainer's behalf): **#543 (1, 3) → #544 `azp` (2) → #527 (7, 8,
9)**; then, deferred, #544 rotation (10, 11) and #546 (4, 5, 6). Slice 1 goes first: its lock order is the rule every
later multi-row write (6, 8, 11) is written against. Because #527 lands before #546, slice 8 ships without "finishing
owed undos", and slice 6 adds that job to the sweeper. Each slice moves the CHANGELOG lines of §7 that it lands, and
closes its issue only with its last slice (#543 → 3, #527 → 9). #544 stays open after slice 2 for its deferred
rotation, and #546 stays open whole.

## 11. Sources

- RFC 9700, *OAuth 2.0 Security Best Current Practice*, §2.2.2 and §4.14 — https://www.rfc-editor.org/rfc/rfc9700.html
- RFC 7009, *OAuth 2.0 Token Revocation*, §2.1 — https://www.rfc-editor.org/rfc/rfc7009.html
- RFC 9470, *OAuth 2.0 Step Up Authentication Challenge Protocol* — https://www.rfc-editor.org/rfc/rfc9470.html
- RFC 7517 (JWK) §4.5, §5; RFC 7518 (JWA) §3.4; RFC 7638 (JWK thumbprint); RFC 9068 (JWT access tokens)
- OpenID Connect Core 1.0 §2 (`azp`), §3.1.3.7, §10.1.1 — https://openid.net/specs/openid-connect-core-1_0.html
- Google, *Verify the Google ID token on your server side* — https://developers.google.com/identity/gsi/web/guides/verify-google-id-token
- Google Cloud, *Token types* (service-account ID tokens: free `aud`, `azp` = the account's unique id) — https://docs.cloud.google.com/docs/authentication/token-types ;
  `projects.serviceAccounts.generateIdToken` — https://docs.cloud.google.com/iam/docs/reference/credentials/rest/v1/projects.serviceAccounts/generateIdToken
- NIST SP 800-63B-4 §5.2 (reauthentication), §2.2.3 — https://pages.nist.gov/800-63-4/sp800-63b/session
- Auth0, *Refresh Token Rotation* (family invalidation, `ferrt`, reuse interval) — https://auth0.com/docs/secure/tokens/refresh-tokens/refresh-token-rotation
- Supabase, *User sessions* (10 s reuse interval; the session terminated on reuse) — https://supabase.com/docs/guides/auth/sessions ;
  *JWT Signing Keys* (standby → current → previously used → revoked) — https://supabase.com/docs/guides/auth/signing-keys
- Firebase Admin, *Manage user sessions* (`revokeRefreshTokens`, `tokensValidAfterTime`, `checkRevoked`) — https://firebase.google.com/docs/auth/admin/manage-sessions
- GitHub, *Maintaining ownership continuity for your organization* (an enterprise owner can make themselves an owner) —
  https://docs.github.com/en/organizations/managing-peoples-access-to-your-organization-with-roles/maintaining-ownership-continuity-for-your-organization
- PostgreSQL, *Explicit Locking — Deadlocks* (§13.3.4, "acquire locks on multiple objects in a consistent order"), and
  *SELECT — The Locking Clause* (ordering caveat under `READ COMMITTED`) — https://www.postgresql.org/docs/current/explicit-locking.html
