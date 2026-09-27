# F5 admin — a person the dashboard creates can set a password (todo item 30)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** a person created in Access can redeem the credential token through an unauthenticated set-password
page and then sign in; credentials are rate-limited; a password change ends every other session.

**Architecture:** redemption lives in `MMLib.Alvo.Identity` (`AlvoSignIn.SetPasswordAsync`), the page and the
link in `MMLib.Alvo.Admin` (static SSR, like sign-in), the endpoint and the shared limiter in `MMLib.Alvo.Host`.

**Spec:** `docs/superpowers/specs/2026-09-27-f5-admin-set-password-design.md` (binding; §10 are rulings). The
dashboard's interaction language is `docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md` §3.

## Global Constraints

- Security core: run `.claude/skills/alvo-security-core-review` on every task; default-deny on every path; no
  token, password or typed email in any log; one byte-identical refusal for every `Refused` case.
- Public API grows only by the spec §5 table (Identity: `AlvoSignIn.SetPasswordAsync`, `AlvoPasswordSetOutcome`;
  Admin: `AlvoAdmin.SetPasswordPath`, `AlvoAdmin.SetPasswordEndpoint`); everything else internal.
- Abstractions: remarks only.
- `.cs` BOM + CRLF; `.razor` BOM + LF; methods ≤ ~25 lines; XML docs; comments say why.
- E2E: role / label / test-id selectors only (`EndToEndSelectorTests` at 0).
- Gates: `dotnet test` for Identity.Tests, Admin.Tests, Host.Tests; `scripts/test-ring1`;
  `scripts/test-admin-e2e` whole (T2, T3); `dotnet build -c Release` for touched projects.
- Conventional Commits ending with `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`.

---

### Task 1: Redemption, password policy and sessions in Identity (security core)

**Files:** spec §6 Identity + Abstractions lines. **Do:** spec §3 (policy, `SetPasswordAsync` order, lifetime,
exact `ExpiresAt`, lockout clear, bootstrap refusal at redemption, bootstrap-seed length refusal at start), §4
(stamp check in `AlvoSessionValidation`, cookie and circuit), §10.3 (dummy verification on sign-in's
unknown-email path), remarks. **Tests:** every Identity unit and session case of spec §7, plus: an unknown-email
sign-in calls the hasher exactly once (the counting decorator), like a wrong password. PublicApi Identity grows by
exactly the two §5 symbols.

- [ ] Tests first (red), implement, green; security-core checklist in the report; commit
  `feat(identity): a credential token can be redeemed once, and a password change ends every session`.

### Task 2: The set-password page, the endpoint, the shared limiter and the link

**Files:** spec §6 Admin + Host lines. **Do:** spec §1 (link in the fragment, `SetPasswordLink`, token panel
with Copy link and the disabled note, `admin.js` fragment → inputs → `replaceState`), §2 (page states, headers,
endpoint steps in order, `alvo-admin-credentials` limiter on both posts, `OnRejected` 303 with `throttled=true`,
internal `Alvo:Admin:CredentialAttemptsPerMinute`, pipeline order + comment), the sign-in `passwordSet` notice.
**Tests:** every Host-integration and Admin-unit case of spec §7.

- [ ] Tests first, implement, green; commit `feat(f5): an issued credential token is a link to a set-password page`.

### Task 3: End to end, and the docs

**Do:** the four e2e scenarios of spec §7 (+ extend the second-person scenario), raise the e2e world's attempt
limit, `docs/architecture/host.md` (set-password, limiter, forwarded headers, key ring, policy ≥ 15),
`docs/todo-admin.md` item 30 ✅ (and note §10.3 fixed the sign-in timing oracle).

- [ ] Scenarios, full `scripts/test-admin-e2e` twice, docs; commit `test(f5): a created person sets a password and signs in`.
