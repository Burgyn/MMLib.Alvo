# S4 — Invitations and `invitationOnly` sign-up (level 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> Commit this file as `docs/superpowers/plans/2026-11-13-accounts-s4-invitations.md` in Task 1's commit (master plan's
> plan-file name).

**Goal:** A tenant owner invites people by a secret code shown once; anyone holding the code previews the invitation; a
signed-in person accepts it and becomes a member with the invitation's roles; and under `signUp.mode: invitationOnly` the
token exchange carries the code, so the account, its identity and its membership commit in one transaction — on SQLite
and PostgreSQL, with every dead code answering one identical 404 and no code in any log or URL.

**Architecture:** One more registry table (`{prefix}_tenant_invitations`) in the EF adapter behind the S3 port
`ITenantStore` (five members), its SQL in a new `Internal/InvitationTables.cs`, and one shared acceptance routine
(`Internal/InvitationAcceptance.cs`) run by both the tenant store (the `/invitations/accept` route) and the account store
(the sign-in transaction). The owner's invitations are swept inside S3's member writes when they lose ownership. The core
gains the code (`InvitationCode`: mint, hash, redaction), `SelfServicePolicy.Invitations`, the five routes beside S3's
`/tenants` routes (mapped by `MapAlvoAccounts()` under the auth prefix, S3 D1), and the `Invitations` rate-limit budget.
`MMLib.Alvo.Accounts` reads `invitation_code` on the exchange (an RFC 8693 / RFC 6749 §8.2 extension parameter).

**Tech Stack:** .NET 10, minimal APIs, EF Core relational connections (SQLite + PostgreSQL), `System.Security.Cryptography`
(`RandomNumberGenerator`, `SHA256`), xUnit v3 on MTP, Shouldly, NSubstitute, Verify, `Microsoft.AspNetCore.TestHost`,
`Microsoft.Extensions.TimeProvider.Testing`, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` — read §3.1
(`invitations`, `signUp.mode`), §4 (the invitations row: GitHub/Slack invite links), §5.2 step 4–5, §5.3 (email trust),
§6.1 (the Invitation model), §6.3 (the five invitation routes; codes in bodies), §6.4 (every invariant — each is a fact
below), §7.3 (20/min), §8 (no secret in logs), §11 (S4 row), §11.1, §14 AC4–AC6, §16 B10. Master plan
`docs/superpowers/plans/2026-11-13-accounts-00-master.md` (fixed: `_tenant_invitations`, branch
`accounts/s4-invitations`). S3 plan `…-accounts-s3-tenants.md` — D1 (routes under the auth prefix — carried), D2
(`ITenantStore`, `TenantTables`, no FKs, JSON roles), D3 (the tenant `version` bump), D5 (effective roles recomputed per
request), D7 (who may call; non-member 404 identical to unknown), D8 (problem types), D15 (`TenantSeed`). S2 plan
`…-accounts-s2-sign-in.md` — D14 (`invitationOnly` refused every new identity until now), the token endpoint's error
vocabulary (spec D4). Contributor view: `docs/architecture/accounts.md`. Prior art read for this plan: GitHub
organisation invitations and Slack invite links (secret code, expiry, use count, revocable, shown to the inviter once);
GitHub secret-scanning token prefixes (spec §7.1's `alvo_pat_`); RFC 6749 §5.2 (`invalid_grant`: "invalid, expired,
revoked"), §8.2 (extension parameters); RFC 8693 §2.1 (the request may carry further parameters); OWASP *Forgot password*
/ *Authentication* cheat sheets (one answer for every failed secret lookup; store only a hash of a high-entropy secret).

## Decisions (read before Task 1)

- **D1 — Routes under the auth prefix, mapped by `SelfServiceEndpoints.Map` (S3 D1, carried).**
  `{AlvoAccountsOptions.Prefix}/tenants/{tenantId}/invitations` (POST, GET), `…/invitations/{invitationId}` (DELETE),
  `{prefix}/invitations/preview` (POST), `{prefix}/invitations/accept` (POST) — default `/auth/v1/…`. The same
  **recorded deviation** from §6.3's `{prefix}` as S3; no TeaPie path pin moves (a host without Accounts is unchanged).
- **D2 — The code.** `alvo_inv_` + base64url (no padding) of 16 bytes from `RandomNumberGenerator` = 128 bits (§6.1), 31
  characters. The prefix is GitHub's secret-scanning precedent (spec §7.1 names `alvo_pat_`/`alvo_svc_`). Stored as
  `code_hash` = lowercase hex SHA-256 of the presented string's UTF-8, `UNIQUE`. Unsalted SHA-256 is enough because the
  secret has 128 bits of entropy (no dictionary to precompute) — the spec's choice, restated. **Any** presented string is
  hashed and looked up; a malformed code is simply not found (one path, one answer). The store never sees a code, only
  its hash. `InvitationCode.ToString()` and every record that carries a code print `alvo_inv_***` (`ApiKeyRecord.PrintMembers`
  precedent).
- **D3 — One dead answer.** Unknown, expired, revoked, used up, bound to another (or untrusted) email, into a personal
  tenant, or whose creator is no longer an owner — `preview` and `accept` answer `ProblemResultFactory.NotFound()`
  (404 `not-found`), byte-identical to each other and to S3's no-such-tenant answer. On the token endpoint every dead code
  is one `invalid_grant` (RFC 6749 §5.2 — "invalid, expired, revoked"; type `token-refused`, D4 of the spec) with the fixed
  description `"The invitation is not valid. Sign in without it, or ask for a new one."`. **No new problem slug.**
- **D4 — The acceptance is one atomic routine shared by two transactions** (spec §6.4, §11.1). `InvitationAcceptance.AcceptAsync(connection, transaction, tables, codeHash, person, now)`, in this order:
  (1) `LockAccountSql` on the person (S3 D3; zero rows → `NoAccount`) — serialises one person's concurrent accepts;
  (2) read the **live** invitation: `code_hash = @h AND revoked_at IS NULL AND expires_at > @now AND uses < max_uses`,
  joined to its tenant **with `t.kind = 'shared'`** (spec §6.4: personal tenants are never invited into — enforced here,
  the DB does not) and `EXISTS` an owner membership of `created_by` (belt and braces to D6) — none → `Dead`;
  (3) email-bound → read the account's `email`, `email_trusted` (S2's `AccountSql`) and compare in C#
  `OrdinalIgnoreCase` — untrusted or different → `Dead` (§5.3: only a trusted email satisfies it);
  (4) already a member → `AlreadyMember` with the stored membership — **no use consumed, roles unchanged** (§6.3);
  (5) `UPDATE … SET uses = uses + 1 WHERE id = @id AND uses < max_uses AND revoked_at IS NULL AND expires_at > @now` —
  zero rows → re-read the person's membership (a concurrent accept of their own may have committed) → `AlreadyMember`
  or `Dead`; (6) insert the membership with `roles`, `is_owner = grants_owner`, `invitation_id` → `Joined`.
  Never read-then-write for the use count: step 5's predicate is the decision. On PostgreSQL the loser of a race waits on
  the invitation's row lock and re-evaluates; on SQLite the write gate serialises outright.
- **D5 — `invitationOnly` sign-up** (§6.4). The exchange accepts `invitation_code` in any `signUp.mode`. Order in
  `ExchangeGrant`: parameters → verify the subject token (no anonymous probing of codes) → `invitation_code` while
  `SelfServicePolicy.Invitations` is off → 400 `invalid_request` `"This backend takes no invitations; send no invitation_code."`
  → sign-up decision → store. `SignUpDecision.Decide(policy, identity, hasInvitation)`: `invitationOnly` without a code →
  `InvitationOnly` (S2 D14, unchanged); with a code, or `open` → the `allowedEmailDomains` check → `Admitted` |
  `DomainNotAllowed`. (Domains now apply under `invitationOnly` too — §3.1 says "may sign up" without a mode; nobody was
  admitted under it before, so nothing regresses.) The store runs D4 inside `SignInAsync`'s transaction after the
  session and the personal tenant; `Dead`/`NoAccount` → `SignInStatus.InvitationRefused`, **nothing commits** — no
  account, no identity, no session.
- **D6 — An owner's invitations die with their ownership, and creating one is serialised with that** (§6.4, B10).
  `UpdateMemberAsync` (demotion of an owner) and `RemoveMemberAsync` (an owner removed **or leaving**) run
  `UPDATE {invitations} SET revoked_at = @now WHERE tenant_id = @t AND created_by = @member AND revoked_at IS NULL` in the
  same transaction. `CreateInvitationAsync` and `RevokeInvitationAsync` go through S3's `MemberWriteAsync` (the tenant
  `version` bump first), so a create racing its creator's demotion is serialised: either the create sees a non-owner
  (`NotOwner`) or the demotion's sweep sees the new row. D4 step 2's `EXISTS` is the second line.
- **D7 — An existing account with a code.** The exchange accepts the invitation for whichever account signs in, new or
  existing, in the sign-in transaction. **All or nothing**: a dead code refuses the whole exchange (D3), even for an
  account that would otherwise sign in — the client asked for two things atomically; it signs in again without the code.
  Recorded so a reader does not take it for an accident.
- **D8 — Preview is anonymous.** `POST {prefix}/invitations/preview` reads no credential (spec: "anyone holding the
  code") — under `invitationOnly` the invitee has no account yet. It cannot judge an email binding, so it answers
  `emailBound: true|false` and never the address; `accept` judges the binding. **Recorded deviation**: §6.4's "email-bound
  to someone else → the same 404" holds for `accept` and the exchange, not for an anonymous preview, which has no someone.
- **D9 — Owner rules on create** (§6.4): `roles` required, unique, each in `assignableRoles` (may be empty);
  `isOwner` default `false`; `maxUses` default 1, range 1–100 (builder's bound — the spec gives only the default);
  `expiresAt` RFC 3339 with an offset, default `now + 7 days`, must be `> now` and `≤ now + 30 days`. With `isOwner: true`:
  `maxUses` must be 1 and `expiresAt ≤ now + 7 days` — **refused, never clamped** (422 at `/maxUses`, `/expiresAt`).
  `email` optional: trimmed, ≤ 254 chars, exactly one `@` with non-empty sides, no whitespace or control characters,
  stored lower-invariant. Personal tenant → 409 `conflict` (S3's sentence). Every violation is a fixed sentence that
  echoes nothing sent.
- **D10 — Who may call** (S3 D7, D8): create/list/revoke — a person (`SelfServiceCall.EnterAsync`), a member who is
  an **owner** (403 `forbidden` otherwise), non-member/unknown tenant → identical 404. Any owner revokes any invitation of
  the tenant; revoking twice is 204 (first `revoked_at` kept); an invitation id of another tenant → 404. Accept — a person
  (API key → 403; nothing → 401 Bearer challenge, S3's gate). Preview — anyone (D8). Feature: every one of the five routes
  answers 404 `feature-disabled` with `descriptorKey` `tenancy.selfService.invitations` while `SelfServicePolicy.Invitations`
  is off (`selfService` off first names `tenancy.selfService`, S3's order).
- **D11 — Responses.** Create → **201** with `{ id, code, roles, isOwner, email, maxUses, uses, expiresAt, createdBy,
  createdAt, status }` — the **only** answer that ever carries `code`; no `Location` (there is no single-invitation GET).
  List → 200, newest first, the same shape **without `code`**, plus `revokedAt`; `status` = `revoked` > `usedUp` >
  `expired` > `active`. Owners see every invitation of the tenant (they need to see what to revoke). Preview → 200
  `{ tenantName, inviterName, roles, isOwner, expiresAt, emailBound }` (roles effective per S3 D5). Accept → 200 the
  person's `MembershipResponse` (S3), for `Joined` and `AlreadyMember` alike (idempotent). Revoke → 204. All with
  `Cache-Control: no-store` (S3's route filter).
- **D12 — `created_at` is a column the spec's model does not list** — builder's detail (§15), for the list's order and
  the owner's view. The table has no foreign keys (S2/S3 precedent).
- **D13 — Rate limit.** `AlvoRateLimitOptions.Invitations` (public, default 20, §7.3/§11.2's `Invitations`) and
  `AlvoRateLimit.Invitations`, charged on **all five** invitation routes after CORS and no-store, before any gate (S3's
  order for `Create`). The exchange with `invitation_code` stays on the `Token` budget.
- **D14 — `invitations` is honoured.** S3 D11's `WithinBlocks` row and the dashboard's `DeclaredSlots.SelfServiceInvitations`
  mirror are removed; `scripts/gen-prototype-fixtures` regenerates `capabilities.warned`. `SelfServicePolicy` gains
  `bool Invitations` (`Enabled && CreatesTenants && descriptor says true`).
- **D15 — Membership provenance** (spec D9): an accepted membership's `invitation_id` is the invitation's id; S3's
  `InsertMemberSql` keeps `NULL` for creators and personal owners. No membership event (D9 of the spec defers them).

## Global Constraints

- English in everything committed. CRLF + BOM in every new `.cs`/`.csproj` (copy a neighbour's bytes; re-normalise after
  any Bash/Python edit). Short single-purpose methods (~25 lines); no inline comments by default (`alvo-dotnet-conventions`).
- Public API is the contract. Allowed public growth, justified per line in its commit body: core —
  `AlvoRateLimitOptions.Invitations`. **Nothing else public**: no new `AlvoProblemTypes` slug (D3), ports and records
  `internal`. No new `InternalsVisibleTo` direction.
- Default-deny: `invitations` absent → every invitation route `feature-disabled`, and `invitation_code` refused on the
  exchange; `invitationOnly` without a live code creates nothing.
- Tables only through `AlvoFrameworkTables` (`TenantInvitationsSuffix = "_tenant_invitations"`); one DDL text for both
  engines (TEXT ids/instants in `StoredInstant.Text` form, INTEGER 0/1 booleans, roles as a JSON array); every value a
  bound parameter.
- **No code in a URL, a log, a `ToString` or a refusal** (§6.3, §8). The store receives hashes only.
- **Test discipline:** inner loop = the affected project filtered to the class,
  `dotnet test --project <proj> -- --filter-class '*<Class>*'`; ring0 only through the pre-commit hook; ring1 and ring2
  once, in Task 9. Docker for the PostgreSQL legs. Branch `accounts/s4-invitations` from `accounts/s3-tenants`; never push
  to `main`; the controller opens the PR (targeting `accounts/s3-tenants`).
- A moved baseline is accepted only in the task that moves it, judged by `alvo-snapshot-judge` when the Stop hook asks.
- **Mutation carry-over:** every new EF file (`EfCoreTenantStore.Invitations.cs`, `Internal/InvitationTables.cs`,
  `Internal/InvitationAcceptance.cs`) goes into `stryker-config.accounts-store.json`'s `mutate` and is excluded (`!…`)
  from `stryker-config.data-ef-rest.json` in the task that creates it — its facts compile into Data.Sqlite.Tests, never
  into EntityFrameworkCore.Tests (the S2/S3 reasoning in `mutation.yml`'s header).
- Do not touch `examples/field-service`, its TeaPie stacks, the website or the `alvo-descriptor-*` skills (S6).

## Review Focus

1. **Two people accept a single-use invitation at the same instant** — exactly one membership, the other a dead 404;
   and the same race through the exchange under `invitationOnly`: exactly one account. Pinned by Tasks 2 and 3 (store,
   both engines, 20 iterations) and Task 8 (HTTP, both engines).
2. **An owner is demoted or removed while an invitation of theirs is created or accepted** — no invitation of a
   non-owner stays live, and nobody joins through one after the sweep committed. Pinned by Task 1 (concurrent
   create-vs-demote) and Task 2 (accept after sweep).
3. **One person double-submits accept** — one membership, both answers 200, one use consumed. Pinned by Task 2.
4. **A dead code on an `invitationOnly` sign-up** — no account, no identity, no session row, no membership. Pinned by
   Task 3 (row counts) and Task 7 (HTTP).
5. **Every dead reason, one answer** — unknown, malformed, expired, revoked, used up, other email, untrusted email,
   personal tenant, creator no longer an owner: byte-identical 404 bodies. Pinned by Task 6.

---

### Task 1: The invitation store I — table, creating, listing, revoking, and the ownership sweep

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` (`TenantInvitationsSuffix`; `NamesFor`)
- Modify: `src/MMLib.Alvo.Abstractions/Tenants/ITenantStore.cs` (three members; `MemberChangeStatus.NoSuchInvitation`)
- Create: `src/MMLib.Alvo.Abstractions/Tenants/TenantInvitations.cs` (the records below, namespace `MMLib.Alvo.Tenants`)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/InvitationTables.cs`,
  `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreTenantStore.Invitations.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreTenantStore.cs` (`EnsureTablesAsync` creates the invitations
  table), `EfCoreTenantStore.Members.cs` (the D6 sweep), `Internal/SystemSchemaInitializer.cs` (table + index),
  `Internal/TenantTables.cs` (doc: `invitation_id` is S4's; nothing else)
- Modify: `stryker-config.accounts-store.json`, `stryker-config.data-ef-rest.json`, `.github/workflows/mutation.yml`
  (header paragraph names the invitation files; `accounts-store` `mutants` estimate grows by the new files' code lines ×
  0.16, comment says so — still an estimate, re-probe on the first run)
- Create: `test/_shared/ef/InvitationStoreFacts.cs` (abstract), `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteInvitationStoreTests.cs`,
  `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlInvitationStoreTests.cs` (link the shared file in both
  csproj — the `TenantStoreFacts` precedent); Modify: `test/_shared/ef/TenantSeed.cs` (`AddInvitationAsync`, D4's
  personal-tenant fact; `InvitationIdOfAsync`, D15)

**Interfaces (all `internal`):**
```csharp
// ITenantStore gains:
Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct);
Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct); // newest first; the route checks ownership (S3 MemberRoutes precedent)
Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct);

sealed record CreateInvitationRequest(
    Guid Id, TenantId Tenant, UserId Actor, string CodeHash, IReadOnlyList<string> Roles, bool GrantsOwner,
    string? Email, int MaxUses, DateTimeOffset ExpiresAt, DateTimeOffset Now);
readonly record struct CreateInvitationOutcome(MemberChangeStatus Status, InvitationView? Invitation);
sealed record InvitationView(
    Guid Id, TenantId Tenant, IReadOnlyList<string> Roles, bool GrantsOwner, string? Email, int MaxUses, int Uses,
    DateTimeOffset ExpiresAt, UserId CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
sealed record InvitationRevocation(TenantId Tenant, UserId Actor, Guid Invitation, DateTimeOffset Now);
// MemberChangeStatus gains NoSuchInvitation (the invitation is not one of this tenant's).
```
- DDL (prefix `p`): `p_tenant_invitations(id TEXT PK, tenant_id TEXT NOT NULL, code_hash TEXT NOT NULL UNIQUE, roles
  TEXT NOT NULL, grants_owner INTEGER NOT NULL, email TEXT NULL, max_uses INTEGER NOT NULL, uses INTEGER NOT NULL,
  expires_at TEXT NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL, revoked_at TEXT NULL)` + index
  `tenant_id`. `InvitationTables(string schemaPrefix)` holds the DDL and every statement (`InsertSql`, `ListSql`,
  `RevokeSql` = `… SET revoked_at = COALESCE(revoked_at, @now) WHERE id = @id AND tenant_id = @tenant_id`,
  `RevokeCreatedBySql` (D6), and Task 2's `LiveSql`, `ConsumeSql`, `InsertInvitedMemberSql`), plus `ReadView`.
- `CreateInvitationAsync` through `MemberWriteAsync` (D6): bump → actor's membership (none → `NotFound`) → personal →
  `PersonalTenant`; not owner → `NotOwner`; insert (`uses` 0) → `Changed` with the view. The store stores what it is
  given: roles verbatim, the D9 bounds are the core's.
- `RevokeInvitationAsync` through `MemberWriteAsync`: not owner → `NotOwner`; `RevokeSql` zero rows → `NoSuchInvitation`;
  else `Changed` (an already-revoked one too — idempotent, first instant kept).
- D6 sweep: `UpdatedAsync` after `WriteUpdateAsync` when `update.IsOwner == false && member.IsOwner`; `RemovedAsync` after
  the delete when `member.IsOwner` (removal and leaving alike).

- [ ] **Step 1: Write the failing facts** in `InvitationStoreFacts` (accounts through `IAccountStore.SignInAsync`, tenants
  through `CreateSharedAsync`, members through `TenantSeed`):
  `An_owner_creates_an_invitation_with_its_hash_roles_and_bounds` (view round-trips; `uses` 0; `RevokedAt` null);
  `A_member_who_is_not_an_owner_cannot_invite` (`NotOwner`, no row); `A_non_member_and_an_unknown_tenant_get_not_found`;
  `A_personal_tenant_takes_no_invitation` (`PersonalTenant`, no row);
  `Invitations_list_newest_first_with_every_state`;
  `An_owner_revokes_any_invitation_of_the_tenant_once_and_again` (second revoke `Changed`, `RevokedAt` unchanged);
  `Revoking_another_tenant_s_invitation_is_no_such_invitation`;
  `Demoting_an_owner_revokes_the_invitations_they_created_and_no_one_else_s`;
  `Removing_an_owner_or_an_owner_leaving_revokes_their_invitations`;
  `A_refused_demotion_revokes_nothing` (`LastOwner` → rows untouched — the sweep rolls back with it);
  `An_invitation_created_while_its_creator_is_demoted_never_outlives_the_demotion` (**Review Focus 2**: two owners A, B;
  `Task.WhenAll(A creates, B demotes A)`, 20 iterations → either `NotOwner` and no row, or a row with `revoked_at` set);
  `The_invitations_table_is_a_reserved_framework_name`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteInvitationStoreTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement**, including the two stryker configs and the `mutation.yml` comment.
- [ ] **Step 4: Run** the Step 2 command, then
  `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlInvitationStoreTests*'`,
  `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteTenantStoreTests*'` (S3's member facts
  unchanged) and `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*Introspect*'`.
  Expected: PASS. **Prove the serialisation matters:** route `CreateInvitationAsync` around `MemberWriteAsync` (no bump)
  → the Review Focus 2 fact goes red on PostgreSQL; restore; record it in the commit body.
- [ ] **Step 5: Commit** (with this plan file) — `feat(data-ef): owners create and revoke invitations, and an owner's die with their ownership`

### Task 2: The invitation store II — preview and the atomic accept

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Tenants/ITenantStore.cs`, `…/Tenants/TenantInvitations.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/InvitationAcceptance.cs`
- Modify: `Internal/InvitationTables.cs`, `EfCoreTenantStore.Invitations.cs`, the two stryker configs (the new file)
- Test: `test/_shared/ef/InvitationStoreFacts.cs`

**Interfaces:**
```csharp
Task<InvitationPreview?> PreviewInvitationAsync(string codeHash, DateTimeOffset now, CancellationToken ct); // null: dead (D3)
Task<AcceptInvitationOutcome> AcceptInvitationAsync(AcceptInvitationRequest request, CancellationToken ct);

sealed record InvitationPreview(
    TenantId Tenant, string TenantName, string? InviterName, IReadOnlyList<string> Roles, bool GrantsOwner,
    bool EmailBound, DateTimeOffset ExpiresAt);
sealed record AcceptInvitationRequest(string CodeHash, UserId Person, DateTimeOffset Now);
enum AcceptInvitationStatus { Joined, AlreadyMember, Dead, NoAccount }
readonly record struct AcceptInvitationOutcome(AcceptInvitationStatus Status, StoredMembership? Membership);

internal static class InvitationAcceptance   // EF Internal
{
    internal static Task<AcceptInvitationOutcome> AcceptAsync(
        DbConnection connection, DbTransaction transaction, InvitationTables invitations, TenantTables tenants,
        AuthTables auth, string codeHash, UserId person, DateTimeOffset now, CancellationToken ct);
}
```
- `LiveSql` (D4 step 2): `i ⨝ tenants t` on `t.id = i.tenant_id`, `t.kind = 'shared'`, the four liveness predicates, and
  `EXISTS (SELECT 1 FROM {members} o WHERE o.tenant_id = i.tenant_id AND o.user_id = i.created_by AND o.is_owner = 1)`.
  Preview runs the same predicate (no lock, no transaction), left-joining `{auth_accounts}` on `created_by` for the
  inviter's display name.
- `AcceptInvitationAsync` = `InTransactionAsync(… InvitationAcceptance.AcceptAsync …)`, committed on `Joined`.
- The routine follows D4's six steps exactly; each step a private method (~25-line ceiling).

- [ ] **Step 1: Write the failing facts:**
  `Accepting_makes_a_member_with_the_invitation_s_roles_ownership_and_provenance` (membership kind shared, roles, owner
  per `grants_owner`; `TenantSeed.InvitationIdOfAsync` = the invitation id; `uses` 1);
  `Accepting_twice_is_already_a_member_and_consumes_nothing` (second → `AlreadyMember`, `uses` still 1, roles unchanged
  after an owner changed them);
  theory `Every_dead_invitation_previews_and_accepts_as_dead` (unknown hash; expired — `Now` = `expires_at` exactly and
  after; revoked; used up at `max_uses` 2 after two people; creator demoted; creator removed) → preview `null`, accept
  `Dead`, no membership;
  `An_invitation_into_a_personal_tenant_is_dead_even_when_its_row_exists` (`TenantSeed.AddInvitationAsync` into a
  personal tenant, bypassing create) → `Dead`;
  `An_email_bound_invitation_admits_only_that_trusted_email` (bound `ada@gmail.com`: Ada trusted → `Joined`;
  `ADA@Gmail.com` trusted → `Joined` on a second invitation; the same address untrusted → `Dead`; another address →
  `Dead`; no email → `Dead`);
  `A_disabled_or_unknown_person_accepts_nothing` (`NoAccount`, `uses` 0);
  `Preview_names_the_tenant_the_inviter_the_roles_and_whether_an_email_is_bound`;
  `Two_people_accepting_a_single_use_invitation_at_once_make_one_member` (**Review Focus 1**: `Task.WhenAll`, 20
  iterations on fresh invitations → statuses `{Joined, Dead}`, one membership, `uses` 1);
  `Five_people_accepting_a_three_use_invitation_at_once_make_three_members`;
  `One_person_accepting_twice_at_once_joins_once` (**Review Focus 3**: `{Joined, AlreadyMember}`, `uses` 1);
  `An_accept_racing_its_creator_s_demotion_never_joins_after_the_sweep` (**Review Focus 2**: 20 iterations → either
  `Joined` then the invitation revoked, or `Dead` and no membership).
- [ ] **Step 2: Run to see them fail** — the Task 1 SQLite command; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the SQLite and PostgreSQL commands of Task 1; Expected: PASS. **Prove the conditional update is
  the decision:** drop `AND uses < max_uses` from `ConsumeSql` (keep it in `LiveSql`) → the Review Focus 1 fact goes red
  on PostgreSQL; drop the account lock → the Review Focus 3 fact goes red on PostgreSQL; restore both; record the red runs
  in the commit body.
- [ ] **Step 5: Commit** — `feat(data-ef): accepting an invitation is one conditional update, and a dead one is just dead`

### Task 3: The sign-in transaction accepts an invitation

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Accounts/IAccountStore.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreAccountStore.cs`
- Test: `test/_shared/ef/AccountStoreFacts.cs`

**Interfaces:**
- `SignInRequest` gains a last positional parameter with a default, `SignInInvitation? Invitation = null` (existing call
  sites compile unchanged); `internal sealed record SignInInvitation(string CodeHash)`.
- `SignInStatus.InvitationRefused` — the invitation was dead (or its account could not be locked); nothing was written.
- `SignedInAsync`: after `PersonalTenantEnsuredAsync`, when `request.Invitation` is set, `InvitationAcceptance.AcceptAsync`
  (D4) with the account; `Dead`/`NoAccount` → `new SignInOutcome(SignInStatus.InvitationRefused, null)` — the commit
  predicate (`Account is not null`) rolls the account, identity, session, refresh token and personal tenant back.
  The account row's `email`/`email_trusted` were refreshed earlier in the same transaction, so D4 step 3 reads this
  sign-in's identity.

- [ ] **Step 1: Write the failing facts:**
  `A_new_identity_with_a_live_invitation_creates_account_identity_session_and_membership_together` (`MayCreate: true`,
  invitation set → `Created`; `FindMembershipAsync` → the invitation's tenant and roles);
  `A_dead_invitation_creates_nothing` (**Review Focus 4**: `InvitationRefused`; `CountAsync` of `_auth_accounts`,
  `_auth_identities`, `_auth_sessions`, `_auth_refresh_tokens`, `_tenants`, `_tenant_members` unchanged; `uses` 0);
  `An_existing_account_with_a_live_invitation_joins_while_signing_in`;
  `An_existing_account_with_a_dead_invitation_does_not_sign_in` (D7: no session written);
  `An_email_bound_invitation_is_judged_by_this_sign_in_s_identity` (the account's earlier email untrusted, this sign-in's
  trusted → `Created`/`SignedIn`; reversed → `InvitationRefused`);
  `Two_new_identities_racing_for_one_single_use_invitation_create_one_account` (**Review Focus 1**, 20 iterations →
  `{Created, InvitationRefused}`, one new account);
  `A_sign_in_without_an_invitation_is_unchanged` (S2's facts keep passing untouched).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAccountStoreTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, the PostgreSQL `*PostgreSqlAccountStoreTests*` command, and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests` (no behaviour change yet). Expected: PASS.
- [ ] **Step 5: Commit** — `feat(data-ef): an invited sign-in commits account, identity and membership together or nothing`

### Task 4: The code, the policy, the budget — and `invitations` is honoured

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/InvitationCode.cs`
- Modify: `src/MMLib.Alvo/Tenants/SelfService/SelfServicePolicy.cs` (`Invitations`), `SelfServiceProblems.cs`
  (`InvitationsKey`, `InvitationsOff`)
- Modify: `src/MMLib.Alvo/Api/AlvoRateLimitOptions.cs` (`Invitations = 20`), its validator (`>= 1`),
  `src/MMLib.Alvo/Api/Internal/AlvoRateLimits.cs` (`AlvoRateLimit.Invitations`)
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` (remove the D11 row and its remarks sentence),
  `src/MMLib.Alvo.Admin/Components/Home/DeclaredSlots.cs` (remove `SelfServiceInvitations` from the slots and `Qualified`)
- Regenerate: `docs/design/f5-admin/generated/*` via `scripts/gen-prototype-fixtures`
- Test: `test/MMLib.Alvo.Tests/Tenants/InvitationCodeTests.cs`, `test/MMLib.Alvo.Tests/Tenants/SelfServicePolicyTests.cs`,
  `test/MMLib.Alvo.Tests/Descriptor/UnhonouredKeysTests.cs`, `test/MMLib.Alvo.Admin.Tests/Home/DeclaredLimitsTests.cs`, the
  core options suite

**Interfaces:**
```csharp
internal readonly struct InvitationCode
{
    internal const string Prefix = "alvo_inv_";
    internal static InvitationCode New();                 // 16 bytes RandomNumberGenerator, base64url, no padding
    internal static string Hash(string presented);        // lowercase hex SHA-256 of UTF-8; any string
    internal string Value { get; }                        // the only accessor that yields the secret
    internal string Hashed { get; }                       // Hash(Value)
    public override string ToString() => Prefix + "***";
}
```
- `SelfServicePolicy` gains a positional `bool Invitations` (`None` → `false`); `From` sets it from
  `selfService.invitations == true && CreatesTenants`.
- `SelfServiceProblems.InvitationsKey = "tenancy.selfService.invitations"`; `InvitationsOff(policy)` =
  `SelfServiceOff(policy) ?? (policy.Invitations ? null : FeatureDisabled(InvitationsKey))`.

- [ ] **Step 1: Write the failing facts.** `InvitationCodeTests`: `A_code_is_the_prefix_and_128_bits_in_base64url`
  (length 31, alphabet, 1 000 codes distinct); `The_hash_is_lowercase_hex_sha256_of_the_presented_text` (a known
  vector); `A_code_never_prints_itself` (`ToString`, string interpolation, `$"{code}"` → `alvo_inv_***`).
  `SelfServicePolicyTests`: `Invitations_are_on_only_with_create_tenants_and_the_key`.
  `UnhonouredKeysTests`: the S3 fact becomes `Invitations_are_honoured` (no warning). `DeclaredLimitsTests`: the mirror.
  Options: `Invitations = 0` refused; the default is 20.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*InvitationCodeTests*' --filter-class '*SelfServicePolicyTests*' --filter-class '*UnhonouredKeysTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement**; then `scripts/gen-prototype-fixtures` and `scripts/gen-prototype-fixtures --check` (clean).
  Sweep for the claim: `grep -rn "SelfServiceInvitations\|invitations arrive with S4\|until S4\|unhonoured until S4" src test docs/architecture`
  → only history.
- [ ] **Step 4: Run** the Step 2 command, `dotnet test --project test/MMLib.Alvo.Admin.Tests -- --filter-class '*DeclaredLimitsTests*'`,
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PublicApiApprovalTests*'` and the options suite.
  Expected: PASS after accepting exactly the core PublicApi (`AlvoRateLimitOptions.Invitations`) and any capability
  snapshot the removed row moves (judged).
- [ ] **Step 5: Commit** — `feat(tenancy): invitation codes are 128 bits shown once, and tenancy.selfService.invitations is honoured`

### Task 5: Owners invite, list and revoke — `/tenants/{id}/invitations`

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/InvitationRoutes.cs`, `InvitationRequest.cs` (the D9 body reader),
  `InvitationResponse.cs`
- Modify: `SelfServiceEndpoints.cs` (`MapInvitations`), `SelfServiceProblems.cs` (`NoSuchInvitation` → 404)
- Modify: `src/MMLib.Alvo.Accounts/Internal/AccountsDocumentation.Tenants.cs` (or a new `AccountsDocumentation.Invitations.cs`
  if the file passes ~250 lines), `AccountsSchemas.cs`, `AccountsDocumentTransformer.cs`
- Test: `test/MMLib.Alvo.Tests/Tenants/InvitationRequestTests.cs`, `test/MMLib.Alvo.Accounts.Tests/InvitationRoutesTests.cs`,
  `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`; Modify: `test/_shared/accounts/descriptors/team-tracker.alvo.json`
  (`"invitations": true`; its description drops "without invitations")

**Interfaces:**
- Operation ids (tag `tenants`): `tenants.createInvitation` (`POST /tenants/{tenantId:guid}/invitations`),
  `tenants.listInvitations` (`GET` same path), `tenants.revokeInvitation` (`DELETE /tenants/{tenantId:guid}/invitations/{invitationId:guid}`).
  Each: CORS → no-store → `limits.For(AlvoRateLimit.Invitations)` → `SelfServiceCall.EnterAsync(http, SelfServiceProblems.InvitationsOff)`
  → (create) JSON content type → `InvitationRequest.ReadAsync` → store.
- `InvitationRequest.ReadAsync(HttpRequest, AlvoApiOptions, SelfServicePolicy, DateTimeOffset now, CancellationToken)
  → (InvitationRequest? Value, IReadOnlyList<AlvoViolation>)` per D9 via `SelfServiceBody`; members `roles` (required),
  `isOwner`, `email`, `maxUses`, `expiresAt`; unknown member → violation at its pointer.
  `internal sealed record InvitationRequest(IReadOnlyList<string> Roles, bool IsOwner, string? Email, int MaxUses, DateTimeOffset ExpiresAt)`.
- Create: owner-ship and tenant judged by the store (`CreateInvitationAsync`), the code minted here
  (`InvitationCode.New()`), only `Hashed` sent down; 201 `InvitationResponse` with `code` (D11). `InvitationResponse`
  overrides `PrintMembers` to print `Code = alvo_inv_***`.
- List: `FindMembershipAsync(person, tenant)` → none or not honoured (`SelfServiceMemberships.Honoured`) → 404; personal
  → 409 `conflict`; not owner → 403; else `ListInvitationsAsync` → 200, `status` computed at `call.Now`.
- Revoke: store status → `SelfServiceProblems.Refused` (`NoSuchInvitation` → 404); `Changed` → 204.

- [ ] **Step 1: Write the failing facts.**
  `InvitationRequestTests` (pure): defaults (`maxUses` 1, `expiresAt` now+7d, `isOwner` false); theory
  `Each_bound_is_refused_at_its_pointer` (`roles` missing/not an array/duplicate/`admin`/undeclared → `/roles` or
  `/roles/{i}`; `maxUses` 0, 101, 1.5 → `/maxUses`; `expiresAt` past, now+30d+1s, no offset, not a date → `/expiresAt`;
  `email` `"a"`, `"a@b@c"`, 255 chars, `"a b@c"` → `/email`; an unknown member → its pointer);
  `An_owner_granting_invitation_is_single_use_and_at_most_seven_days` (`isOwner` + `maxUses` 2 → `/maxUses`; `isOwner` +
  now+7d+1s → `/expiresAt`; `isOwner` alone → 1 and now+7d); `No_violation_echoes_what_was_sent`.
  `InvitationRoutesTests` (`AccountsWorld`, `team-tracker` with invitations; a second person seeded into the creator's
  tenant): `An_owner_invites_and_the_code_is_answered_once` (201, `code` starts `alvo_inv_`, list items carry no `code`
  member); `A_member_who_is_not_an_owner_cannot_invite_list_or_revoke` (403 ×3);
  `A_non_member_and_an_unknown_tenant_get_the_same_404` (byte-identical, all three routes);
  `A_personal_tenant_takes_no_invitation` (409 `conflict`);
  `The_list_shows_each_state` (active, expired via `Clock.Advance`, revoked, used up after an accept seeded through the
  store); `Revoking_is_idempotent_and_another_tenant_s_invitation_is_404`;
  `Without_invitations_every_route_is_404_naming_its_key` (re-apply without the key → `descriptorKey`
  `tenancy.selfService.invitations`; without `selfService` → `tenancy.selfService`);
  `An_api_key_is_403_and_no_credential_is_a_bearer_challenge`;
  `Invitation_routes_answer_429_after_their_limit` (`Invitations = 2`; `Retry-After`; `/tenants` and the Data API
  untouched); `A_non_json_body_is_415`; `An_allowed_origin_gets_cors_headers_on_the_invitation_routes`.
  `AccountsOpenApiTests`: the three operations appear once each.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*InvitationRequestTests*'` and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*InvitationRoutesTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 commands and every S3 class that loads `team-tracker`
  (`--filter-class '*TenantRoutesTests*' --filter-class '*MemberRoutesTests*' --filter-class '*PersonResolverTests*'`), then
  `scripts/lint-api`. Expected: PASS after accepting the Accounts OpenAPI snapshot (three operations, judged);
  `[lint-api] OK` (a Vacuum finding is fixed in the transformer, never muted).
- [ ] **Step 5: Commit** — `feat(tenancy): owners invite within assignableRoles, list what they sent, and revoke it`

### Task 6: Anyone previews, a person accepts — `/invitations/preview`, `/invitations/accept`

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/InvitationCodeBody.cs` (`{ "code": string }`), `InvitationPreviewResponse.cs`
- Modify: `InvitationRoutes.cs`, `SelfServiceEndpoints.cs`, the Accounts documentation files of Task 5
- Test: `test/MMLib.Alvo.Accounts.Tests/InvitationAcceptTests.cs`, `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`

**Interfaces:**
- Operation ids (tag `invitations`): `invitations.preview` (`POST /invitations/preview`), `invitations.accept`
  (`POST /invitations/accept`). Order — preview: CORS → no-store → `Invitations` budget → feature (`InvitationsOff`) →
  JSON content type → body → store; **no credential is read** (D8). Accept: CORS → no-store → budget →
  `SelfServiceCall.EnterAsync(http, InvitationsOff)` → content type → body → `AcceptInvitationAsync`.
- `InvitationCodeBody.ReadAsync` → the presented string: exactly one member `code`, a non-empty string (half a surrogate
  pair → violation, `SelfServiceBody.TextOf`); fixed violation messages at `/code`, never the value. The string is
  hashed with `InvitationCode.Hash` and dropped.
- Accept statuses: `Joined`/`AlreadyMember` → 200 `MembershipResponse` (effective roles, S3 D5); `Dead` → `NotFound()`;
  `NoAccount` → 401 `invalid_token` (S3's `NoAccount` answer on `POST /tenants`).
- Preview → 200 `InvitationPreviewResponse` (`tenantName`, `inviterName`, `roles` effective, `isOwner`, `expiresAt`,
  `emailBound`) or `NotFound()`.

- [ ] **Step 1: Write the failing facts** (`team-tracker` with invitations):
  `Anyone_holding_the_code_previews_it_without_signing_in` (no credential → 200; an API key → the same 200);
  `A_person_accepts_and_their_next_request_acts_in_the_tenant_with_its_roles` (accept → 200 membership; `POST /api/jobs`
  with `X-Alvo-Tenant` → 201 for a `dispatcher` invitation, 403 for `technician`);
  `Accepting_again_is_200_with_the_roles_unchanged` (an owner changed the roles in between → still the changed ones);
  `An_owner_granting_invitation_makes_an_owner` (the new owner can invite);
  **`Every_dead_reason_answers_one_byte_identical_404`** (**Review Focus 5**: unknown code; a malformed string; expired
  (`Clock.Advance` 7 days); revoked; used up; email-bound to another address; email-bound to this address untrusted
  (`IdToken(..., emailVerified: false)`); a personal-tenant row seeded by `TenantSeed.AddInvitationAsync`; creator
  demoted — for **preview and accept**, every body and `Content-Type` identical to each other and to S3's
  no-such-tenant 404; none contains the code; preview excludes the two email reasons, per D8, and that exclusion is
  asserted, not skipped);
  `An_email_bound_invitation_admits_its_trusted_address_in_any_case` (`ada@gmail.com` invited, Ada signs in as
  `Ada@Gmail.com`, verified → 200);
  `Removing_an_owner_kills_their_invitations_over_http` (owner B invites, owner A removes B → preview 404; A's own
  invitation still previews);
  `Accept_needs_a_person` (no credential → 401 Bearer challenge; API key → 403);
  theory `A_body_without_one_string_code_is_a_validation_problem` (`{}`, `{"code": 1}`, `{"code": ""}`,
  `{"code": "x", "y": 1}`, a lone surrogate) → 422 at `/code`, body without the value;
  `Without_invitations_preview_and_accept_are_404_naming_its_key`;
  `No_invitation_route_takes_the_code_in_its_url` (`AccountsOpenApiTests`: no path or query parameter on any invitation
  operation; `code` appears only in request bodies and in `tenants.createInvitation`'s 201 schema).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*InvitationAcceptTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, then `scripts/lint-api`; Expected: PASS after accepting the Accounts OpenAPI
  snapshot (two operations); `[lint-api] OK`.
- [ ] **Step 5: Commit** — `feat(tenancy): anyone previews an invitation, a person accepts it, and every dead code is one 404`

### Task 7: `invitationOnly` sign-up — the exchange carries `invitation_code`

**Files:**
- Modify: `src/MMLib.Alvo.Accounts/Internal/SignUpDecision.cs` (`Decide(policy, identity, hasInvitation)`),
  `ExchangeGrant.cs`, `TokenProblems.cs` (`InvitationRefused`, `InvitationsOff`), `AccountsSchemas.cs` (the token form's
  `invitation_code`: "Exchange only, optional: an invitation code to accept in the same transaction (RFC 8693 extension
  parameter). Under `signUp.mode: invitationOnly` it is the only way a new account is created.")
- Modify: `test/_shared/accounts/AccountsWorld.cs` (`ExchangeAsync(string idToken, string? invitationCode = null)`)
- Test: `test/MMLib.Alvo.Accounts.Tests/SignUpDecisionTests.cs`, `test/MMLib.Alvo.Accounts.Tests/InvitationSignUpTests.cs`,
  `test/MMLib.Alvo.Accounts.Tests/TokenExchangeTests.cs`, `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`

**Interfaces:**
- `ExchangeGrant`: `private const string InvitationCodeParameter = "invitation_code";` read through `TokenForm` (empty =
  absent, RFC 6749 §3.2; twice = malformed, S2). Order per D5. The store request carries
  `new SignInInvitation(InvitationCode.Hash(presented))`; `SignInStatus.InvitationRefused` →
  `TokenProblems.InvitationRefused()` = 400 `invalid_grant`, type `token-refused`, D3's sentence. `InvitationsOff()` =
  400 `invalid_request` D5's sentence.
- `SignUpDecision.Decide(SignInPolicy policy, VerifiedIdentity identity, bool hasInvitation)` per D5; the S2 remarks gain
  the `invitationOnly` + code row.

- [ ] **Step 1: Write the failing facts.**
  `SignUpDecisionTests`: `Invitation_only_with_a_code_admits` ; `Invitation_only_without_a_code_refuses_as_before`;
  `Allowed_domains_apply_to_an_invited_sign_up_too`; the S2 facts keep holding with `hasInvitation: false`.
  `InvitationSignUpTests` (`team-tracker` re-applied with `signUp.mode: invitationOnly`; the owner signed in under `open`
  first, the S2 D14 shape):
  `An_invited_person_signs_up_and_is_a_member_at_once` (exchange with the code → 200 tokens; `/user` lists the shared
  tenant with the invitation's roles; a Data API request with the header → 201; `uses` 1);
  `Without_a_code_a_new_person_is_refused_as_before` (403 `sign-up-refused`, `policy` `invitationOnly`; no account row);
  `A_dead_code_refuses_and_creates_nothing` (**Review Focus 4**: unknown, expired, revoked, used up, email-bound to
  another — each 400 `invalid_grant` with one identical body; `CountAsync` of accounts, identities, sessions, refresh
  tokens, tenants and members unchanged);
  `An_email_bound_invitation_signs_up_only_its_trusted_address`;
  `An_existing_person_with_a_code_joins_while_signing_in` and `An_existing_person_with_a_dead_code_is_refused` (D7);
  `A_code_while_invitations_are_off_is_invalid_request` (re-apply without `invitations`);
  `A_code_from_a_refused_subject_token_is_never_consumed` (forged ID token + live code → `invalid_request`; `uses` 0);
  `Allowed_email_domains_refuse_an_invited_outsider` (`time-tracker-kros`-style domains + `invitationOnly` →
  `sign-up-refused` `allowedEmailDomains`; `uses` 0);
  `Open_sign_up_with_a_code_creates_and_joins_in_one_exchange`.
  `TokenExchangeTests`: `Invitation_code_sent_twice_is_a_malformed_form`.
  `AccountsOpenApiTests`: the token form documents `invitation_code`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*SignUpDecisionTests*' --filter-class '*InvitationSignUpTests*' --filter-class '*TokenExchangeTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command and `scripts/lint-api`; Expected: PASS after accepting the Accounts OpenAPI
  snapshot (the form property); `[lint-api] OK`.
- [ ] **Step 5: Commit** — `feat(accounts): under invitationOnly the exchange carries the code, and account and membership commit together`

### Task 8: Invitations end to end on both engines, no code in any log, and the docs

**Files:**
- Create: `test/_shared/accounts/InvitationEngineFacts.cs` (abstract, `Engine`-parameterised, `team-tracker`),
  `test/MMLib.Alvo.Accounts.Tests/SqliteInvitationEngineTests.cs`,
  `test/MMLib.Alvo.Api.Tests.Integration/PostgresInvitationEngineTests.cs` (the csproj links `../_shared/accounts/*.cs`)
- Modify: `test/MMLib.Alvo.Accounts.Tests/SecretsNeverLoggedTests.cs`
- Modify: `docs/architecture/accounts.md` (an S4 section after S3's: what you can build — inviting into a level-3
  tenant and an `invitationOnly` app, with the descriptor; the table; the acceptance routine's six steps; the routes; the
  invariants table below, each row naming its fact; D1–D15 as recorded decisions/deviations; *Not in S4*: tokens (S5),
  membership events (spec D9), invitation emails — Alvo never sends one; the app shares the code),
  `docs/architecture/host.md` (`Alvo:Api:RateLimits:Invitations`), `CHANGELOG.md` `[Unreleased]`, `docs/PLAN.md` (S4 done,
  the initiative's `← YOU ARE HERE` to S5)

**Interfaces:** consumes everything above; produces nothing.

- [ ] **Step 1: Write the facts** in `InvitationEngineFacts`:
  `Level_three_with_invitations_end_to_end` (the owner creates *Acme* → invites a technician → the technician previews
  without signing in → signs in → accepts → reads jobs, cannot create one (403) → the owner revokes a second, unused
  invitation → its preview is 404 → the owner removes the technician → their next request in *Acme* is 401);
  `Invitation_only_end_to_end` (an owner signs up under `open`; re-apply `invitationOnly`; a stranger without a code →
  403; with a dead code → 400 and no account; with a live code → member at once);
  `Two_people_accepting_a_single_use_invitation_at_once_make_one_member` (**Review Focus 1**, over HTTP, `Task.WhenAll`,
  10 iterations → one 200 and one 404 each time);
  `Two_strangers_signing_up_with_one_single_use_code_at_once_make_one_account` (same, through the exchange);
  `A_demoted_owner_s_invitations_are_dead_on_the_next_request`;
  `An_invitation_grants_only_assignable_roles_and_never_admin` (create with `admin` → 422; a stored role dropped from
  `assignableRoles` after acceptance is gone on the next request — S3 D5 through an invited membership);
  `An_invited_tenant_role_never_reaches_a_global_entity_or_management` (the invited `dispatcher` → `POST /api/regions`
  403; Management route with the Bearer → the anonymous answer — spec §6.4's seventh bullet through an invitation).
  `SecretsNeverLoggedTests`: `No_invitation_code_appears_in_any_log` (Trace capture; create two codes → preview, accept,
  accept again, revoke, a dead accept, a validation refusal whose body is near-valid, an `invitationOnly` exchange with a
  live and a dead `invitation_code`, a 429 on preview; scan for each code whole, its 22-character random part, and its
  hash); extend `No_error_body_echoes_a_presented_token` with the dead-code 404, the exchange's `invalid_grant` and a
  `/code` validation refusal — none contains the code.
- [ ] **Step 2: Run them** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*SqliteInvitationEngineTests*' --filter-class '*SecretsNeverLoggedTests*'` and
  `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*PostgresInvitationEngineTests*'`.
  Expected: PASS. **Prove they can fail:** log the presented code at Debug in `InvitationRoutes` → the log fact goes red;
  make D4 skip the `t.kind = 'shared'` predicate → the personal-tenant fact (Task 6) goes red; drop the D6 sweep → the
  demotion fact goes red on both engines; restore all three and record the red runs in the commit body.
- [ ] **Step 3: Docs.** Sweep for the claim, not the file:
  `grep -rn -i "invitations\? \(are\|arrive\|is\) S4\|until S4\|without invitations\|no invitation\|invitationOnly refuses every" docs/architecture src --include='*.md' --include='*.cs'`
  — every claim that invitations do not exist or that `invitationOnly` admits nobody is updated (S2's `SignUpDecision`
  remarks, `accounts.md`'s *Not in S3*, `TenantTables`' `invitation_id` sentence);
  `grep -rn "auth/v1/invitations\|auth/v1/tenants" test/teapie test/teapie-field-service test/teapie-field-service-host`
  → no hits (D1: no pin moves). `scripts/check-brief-freshness` → OK.
- [ ] **Step 4: Commit** — `test(tenancy): invitations end to end on both engines, one dead answer, no code in any log`

### Task 9: Slice gate — Release build, fixtures, ring1, ring2, security-core checklist

**Files:** none new (fixes only, if a gate finds something).

- [ ] **Step 1: Release build of the whole solution** — `dotnet build MMLib.Alvo.slnx -c Release`; Expected: 0 warnings,
  0 errors (CA rules fire in Release only; if the local SDK differs from CI's, repeat inside
  `mcr.microsoft.com/dotnet/sdk:10.0` — the *Rings are Debug, CI is Release* lesson).
- [ ] **Step 2: Prototype fixtures** — `scripts/gen-prototype-fixtures --check`; Expected: exit 0 (Task 4 regenerated
  them; any later edit to `UnhonouredSubsystems.cs`, `AlvoProblemTypes.cs` or the schema must have been followed by a
  regeneration).
- [ ] **Step 3: ring1** — `scripts/test-ring1`; Expected: `[ring1] OK`. The only moved baselines are those Tasks 4–7
  accepted (core PublicApi: one property; the Accounts OpenAPI snapshot; capability fixtures); anything else is a defect.
- [ ] **Step 4: ring2** — `scripts/test-ring2` (Docker up); Expected: exit 0, including `[lint-api] OK` over both documents.
- [ ] **Step 5: `alvo-security-core-review` checklist**, item by item, recorded for the PR report:
  secrets — 128-bit code from `RandomNumberGenerator`, only its SHA-256 stored, shown once (201 only), never in a URL
  (OpenAPI fact), a log (Trace scan), a `ToString` or a refusal; SQL — every invitation statement binds parameters,
  table names only from `AlvoFrameworkTables`; the use count — one conditional `UPDATE`, never read-then-write (Review
  Focus 1, red-proved on PostgreSQL); the wall — accept joins only `kind = 'shared'`, a personal tenant is never entered;
  ownership — owner-granting invitations single-use and ≤ 7 days (refused, not clamped), an owner's invitations swept in
  the demotion/removal transaction and creation serialised with it (Review Focus 2); provenance — invitation roles ⊆
  `assignableRoles` at create, effective roles recomputed per request, `admin` never assignable, owner never in
  `@user.roles`; email — matched only against a trusted email, case-insensitively, never echoed by preview; oracles —
  every dead reason one byte-identical 404, one `invalid_grant` on the exchange, non-member 404 identical to unknown;
  sign-up — `invitationOnly` creates an account only together with a membership, a dead code writes nothing (row counts),
  the subject token is verified before any code is looked up; routes — owner-only management (403), person-only accept,
  anonymous preview reading no credential, feature-disabled names `tenancy.selfService.invitations`, all five routes
  rate-limited (20/min), CORS without credentials. Close with **mark this change `needs-deep-review`**.
- [ ] **Step 6: Hand back** to the controller (plan-guard, PR report, PR targeting `accounts/s3-tenants`). Flag for the
  post-merge mutation run: `accounts-store` grows by three files (its `mutants` stays an estimate until the first real
  run re-probes it, and every other shard's declared counts with it — the *re-probe all shards* lesson); `accounts` grows
  by the exchange's invitation path.

## Self-review against the spec

| Spec requirement | Task |
|---|---|
| §3.1 `invitations` requires `createTenants` (refusal already S3) and is now honoured | 4 |
| §3.1 / §6.4 `invitationOnly`: account + identity + membership commit together; `invitation_code` on the exchange | 3, 7 |
| §6.1 model: `code_hash` SHA-256 of a 128-bit code shown once; roles; `grants_owner`; email; `max_uses` 1; `uses`; `expires_at` 7 d / 30 d; `created_by`; `revoked_at` | 1, 4, 5 |
| §6.3 five routes; codes in bodies, never URLs | 5, 6 |
| §6.4 owner-granting single-use and ≤ 7 days | 5 |
| §6.4 an owner's invitations die with their ownership, same transaction | 1, 2, 8 |
| §6.4 expired / revoked / used-up / email-bound-to-someone-else → the same 404 | 2, 6, 7 |
| §6.4 atomic conditional update, never read-then-write | 2, 3, 8 |
| §6.4 personal tenants never invited into, enforced in the accept path | 1, 2, 6 |
| §5.3 only a trusted email satisfies an email-bound invitation | 2, 3, 6, 7 |
| §7.3 invitation routes 20/min | 4, 5, 6 |
| §8 / §14 AC6 no code in any log | 4, 8 |
| §14 AC4 an invitation grants only assignable roles, never `admin` | 5, 8 |
