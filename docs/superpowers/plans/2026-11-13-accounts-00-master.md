# End-user accounts and self-service tenancy — master plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a person sign in with Google, own data, share a tenant with others, and mint integrator
tokens — chosen per app in the descriptor at three levels.

**Architecture:** Seven stacked slices (S0–S6), one PR each. This master plan fixes what crosses slice
boundaries: names, signatures and invariants. Each slice gets its own detailed plan
(`2026-11-13-accounts-sN-<topic>.md`), written just before that slice starts and against the code the
previous slice actually produced. That is the HX stack's precedent (`2026-11-06-hx-*.md`).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core (SQLite + PostgreSQL),
`Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect` (S2 only, in
`MMLib.Alvo.Accounts`), xUnit on MTP, TeaPie for the e2e stacks.

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` — read it
whole before any slice. It is the authority; this plan only sequences it.

## Global Constraints

- English in everything committed; the maintainer chats in Slovak.
- Conventions live in the `alvo-dotnet-conventions` skill. Short single-purpose methods (~25-line ceiling).
  CRLF + BOM in `.cs` files (fix after any Bash or Python edit, or the pre-commit `dotnet format` check fails).
- Public API is the contract (`alvo-architecture-rules`). Prefer `internal` plus Abstractions'
  `InternalsVisibleTo`. A grown `PublicApi.*.verified.txt` needs a per-symbol justification.
- Default-deny: every new feature is off unless the descriptor turns it on.
- Problem documents through `ProblemResultFactory` conventions. Token-endpoint errors also carry RFC 6749
  `error`/`error_description` (spec D4).
- No secret (ID token, access token, refresh token, invitation code, token secret) in any log or `ToString`.
- Every security-core slice (S0–S5) runs the `alvo-security-core-review` checklist before its PR.
- Tables are named only through `AlvoFrameworkTables` suffixes and created on both engines.
- **Test discipline (the maintainer's explicit instruction):**
  - **Inner loop:** only the affected test project, filtered to the class
    (`dotnet test --project <proj> -- --filter-class <Fqn>`).
  - **ring0:** through the pre-commit hook only.
  - **ring1:** once at the end of a slice.
  - **ring2:** once before the slice's PR.
  - **TeaPie/e2e stacks:** only for slices that change routes (S2–S5 update the OpenAPI pins) or the demo (S6).
- Never push to `main`. Each slice is a branch stacked on the previous one; the PR targets the previous
  slice's branch (S0 targets `main`). A human merges.
- Before each PR: dispatch `alvo-plan-guard`, then build the PR report with the `alvo-pr-report` skill.

## Cross-slice interfaces (fixed here; slices must not rename them)

| Name | Slice | Shape |
|---|---|---|
| `AlvoContext.TenantRoles` | S1 | `IReadOnlySet<Role>`, init-only, default empty. The membership's tenant roles for `AlvoContext.Tenant`. |
| role-evaluation split | S1 | A scoped entity's rules (and a relation into a scoped entity) see `Roles ∪ TenantRoles`. Global entities and Management `access` levels see `Roles` only. |
| `CallerResolution` | S1 | The one place that reads `X-Alvo-Api-Key` / `Authorization: Bearer` for the Data API filter, `CallerGate` and `ManagementCallerFilter` (spec §5.7 table). |
| person resolver key | S1 | A keyed `IAlvoContextResolver` under the key `"alvo-person"`. `Authorization: Bearer` is Alvo's only when this key is registered. |
| `AlvoActorKind.User` | existing | Marks a principal resolved from a person's session. Self-service routes require it. |
| membership resolution | S2 (no tenants) → S3 (tenants) | A core service: `(UserId user, TenantId? requested) → MembershipResolution` (tenant + tenant roles, or refused). S2 returns "no tenant" unless a tenant was requested (refused). S3 reads the registry. |
| `ApiKeyRecord.Kind` | S5 | `ApiKeyKind { Configured, Personal, Service }`; existing records are `Configured`. |
| descriptor keys | S2/S3/S5 | `auth.signUp`, `tenancy.selfService`, `auth.accessTokens`, exactly as spec §3.1. |
| auth prefix | S2 | `AlvoAccountsOptions.Prefix`, default `/auth/v1`. |
| table suffixes | S2/S3/S4 | `_auth_accounts`, `_auth_identities`, `_auth_sessions`, `_auth_refresh_tokens`, `_auth_keys`, `_tenants`, `_tenant_members`, `_tenant_invitations`; S5 adds `_api_keys`. |

## Slices

| # | Branch | Plan file | Deliverable | Gate |
|---|---|---|---|---|
| S0 | `accounts/s0-scoped-ref-tenant` | `…-accounts-s0-scoped-ref-tenant.md` | #161: a scoped → scoped `ref` cannot name another tenant's row. Composite FK, migration, destructive scan. Carries the spec and this plan. | two-tenant adversarial fact; the refusal is indistinguishable from "no such row" |
| S1 | `accounts/s1-role-provenance` | `…-accounts-s1-role-provenance.md` | `TenantRoles`, the evaluation split, one credential reading, Management refusing app tokens, Bearer ignored without a person resolver | every existing suite green; new facts for the split |
| S2 | `accounts/s2-sign-in` | `…-accounts-s2-sign-in.md` | `MMLib.Alvo.Accounts`, Google, sessions, refresh, `/user`, JWKS + metadata, `auth.signUp`, `DisableAccount`, CORS, rate limits | level 1 works end to end |
| S3 | `accounts/s3-tenants` | `…-accounts-s3-tenants.md` | registry, memberships, `tenancy.selfService`, `/tenants`, personal tenant, conditional catalog | levels 2–3 minus invitations |
| S4 | `accounts/s4-invitations` | `…-accounts-s4-invitations.md` | invitations, `invitationOnly` sign-up | level 3 |
| S5 | `accounts/s5-tokens` | `…-accounts-s5-tokens.md` | persisted API keys, personal and service tokens, `/tokens`, `auth.accessTokens` | integrators |
| S6 | `accounts/s6-demo-docs` | `…-accounts-s6-demo-docs.md` | field-service self-service, TeaPie `170-SelfService`, website guides, `docs/architecture/accounts.md`, skills (every slice keeps PLAN.md current) | demo + docs |

## Review Focus

Inputs and conditions no single slice's happy path exercises, each with the expected behaviour and the
slice that pins it:

1. **A person whose membership changes mid-session.** Their next request, and every personal token they
   minted for that tenant, sees the change; a cached principal must never serve it. Pinned by S3 and S5.
2. **Two browser tabs refreshing at the same instant.** Both get the same child, the session survives,
   and reuse after the window kills it. Pinned by S2.
3. **A host that already uses `Authorization` for its own scheme.** Its requests behave exactly as before
   S1 — anonymous or by API key, never 401. Pinned by S1 (a test host with a JwtBearer scheme of its own).
4. **A descriptor that names a tenant role in rules of a global entity or in `access`.** The global rule
   never sees it, and the `access` case is refused at apply. Pinned by S1 (evaluation) and S3 (apply refusal).
5. **Signing in on two replicas at once on first boot.** Exactly one signing key wins, and tokens from
   either replica validate on both. Pinned by S2 (insert-if-absent fact on both engines).
