# A disabled operator keeps acting from an already-open circuit

**Verdict: real vulnerability.** Not intended design, and not a test artefact. It hits every
operator, not only the bootstrap administrator. The e2e reached it through the unguarded key, which
is a legitimate stand-in for "a second administrator disabled me". The bootstrap target is the only
odd part: the guarded path refuses to disable the bootstrap admin, but the defect has nothing to do
with that.

**Severity: High.** It is post-authentication: the attacker must already have a signed-in dashboard
tab open. Within that limit, it defeats a documented revocation in the security core:

- `disabled` does not take effect.
- A change to the operator's tenant does not take effect, so a moved operator keeps acting in their
  old tenant through the circuit's Data gateway. That is a tenancy concern.

Role revocation *is* seen.

Reproduced in `scratchpad/repro/Program.cs` (`dotnet run`, then `dotnet run -- --fixed`).

## 1. How a dashboard call is authorized

- `ManagementGateway.AsOperatorAsync` (`src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs:494-499`)
  calls `CallerAsync` (`:563-567`) before **every** call. Its remark (`:537-560`) says the caller is
  deliberately not cached, so that disabling an operator is a real lockout. `ApplyAsync` (`:177-196`)
  goes through the same path. `DataGateway.cs:52` does the same for data calls.
- `CallerAsync` calls `AuthenticationStateProvider.GetAuthenticationStateAsync()`. That returns the
  cookie's `ClaimsPrincipal`, fixed for the circuit's lifetime, with no revalidating provider
  anywhere in `src/`. It then calls `IAlvoAdminCallerResolver.ResolveAsync`.
- `AlvoAdminCallerResolver` (`src/MMLib.Alvo.Host/Internal/AlvoAdminCallerResolver.cs:31-44`) is
  registered **scoped** (`AlvoHost.cs:124`), so in the dashboard its scope is the circuit. It
  resolves the keyed `IAlvoContextResolver` (`AlvoIdentity.ResolverKey`) from that same circuit
  scope.
- `AlvoIdentityContextResolver.ResolveAsync`
  (`src/MMLib.Alvo.Identity/Internal/AlvoIdentityContextResolver.cs:55-73`) returns `null` when
  `IsDisabled` is set. A `null` caller becomes `AlvoContext.Anonymous`, and the core refuses it.
- The core decides: `ManagementAccessEvaluator.Resolve`
  (`src/MMLib.Alvo/Management/Access/Internal/ManagementAccessEvaluator.cs:60-65`). The bootstrap
  admin maps to `Admin` above the descriptor; everyone else is judged by the descriptor's `access`
  predicates. The bootstrap check only ever sees a principal the resolver already produced, so a
  disabled bootstrap admin *should* be refused like anyone else.

On paper this is correct. It breaks in the store underneath.

## 2. Root cause: EF's identity map in a circuit-lifetime `DbContext`

- `IAlvoUserStore` is `AlvoIdentityUserStore`, registered **scoped**
  (`AlvoIdentityServiceCollectionExtensions.cs:54`). It sits over `UserManager` and
  `AlvoIdentityDbContext`, both scoped (`AddDbContext`, `:85-89`). In the dashboard, all of them
  live as long as the circuit.
- `FindAsync` (`src/MMLib.Alvo.Identity/Internal/AlvoIdentityUserStore.cs:21-23`) calls
  `UserManager.FindByIdAsync`. The EF `UserStore` implements that with `DbSet.FindAsync`, a
  **tracked** lookup. The first call loads the row into the circuit's change tracker. Every later
  call is served from the identity map without a database round trip. Even a tracked query would
  keep the tracked instance's old values.
- `IsDisabled` comes from `LockoutEnd` and `Tenant` from `TenantId` (`:93-101`). Both are read off
  that tracked entity, so a write from any **other** scope is never seen by this circuit. Other
  scopes include:
  - another administrator's circuit
  - `PUT {m}/.../users/{id}/disabled`
  - the CLI
  - the test's unguarded store
- `RoleNames` comes from `GetRolesAsync`, which runs a projection query, so roles are fresh.

Measured with the scratch repro (real SQLite, the real `AddAlvoIdentity` composition, one
long-lived "circuit" scope, and the disable written through `UnguardedKey` in a second scope):

```
circuit, before disable: True
circuit, after disable (same scope): True; tenant seen = none   <- still resolves; tenant change unseen
new scope (new HTTP request), after disable: False
```

## 2a. When a disable is re-checked

| Case | Existing circuit | Next full page load / new HTTP request | Next sign-in |
|---|---|---|---|
| (a) bootstrap admin | **never** (for the circuit's lifetime) | refused | refused (`AlvoAdminSignIn` + resolver) |
| (b) ordinary admin | **never** (same mechanism) | refused | refused |

"Never" has one exception: a disable written *by that same circuit*, because it updates the same
tracked row. That is irrelevant, since nobody disables themselves.

## 2b. What is already tested, and why it misses this

- `test/MMLib.Alvo.Admin.Tests.EndToEnd/RevokedSessionScenarios.cs` disables the operator and then
  calls `session.GoAsync("")`. That is `Page.GotoAsync`, a full page load (`AdminSession.cs:202-206`),
  which means a new circuit, a new scope, a new `DbContext` and a fresh read. It proves the
  new-request case only, never the open-circuit case.
- `AlvoIdentityContextResolverTests` fakes `IAlvoUserStore` (NSubstitute), so tracking can't show.
- `AlvoIdentityUserStoreTests` uses one scope for the whole class, so it writes and reads through
  the same tracker and can't show it either.
- `ManagementGatewayOperatorTests` / `AdminSessionTests` prove the gateway re-resolves on each call,
  which it does. The staleness is one layer below.

## 3. "The unguarded user store"

It is `AlvoUserAdministration.UnguardedKey`
(`src/MMLib.Alvo.Abstractions/Identity/IAlvoUserAdministration.cs:168`). The guarded decorator is
`GuardedUserAdministration` (`src/MMLib.Alvo/Management/Internal/GuardedUserAdministration.cs:83-89`),
and its `EnsureNotBootstrap` refuses to disable the bootstrap admin. The e2e goes around that guard,
as `RevokedSessionScenarios.DisableTheOperatorAsync` already does, because the call has no caller.
That is a test-only path. **The vulnerability, however, is independent of it:** an ordinary admin
disabled through the *guarded* path by a second administrator behaves identically (repro:
non-bootstrap user).

It is not an accepted design either. The docs say the opposite:

- `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md:938`: "role membership, a
  person's tenant, disabled | the identity store | **at once**".
- `:1298` records that the "resolved once per circuit" hole was fixed. That fix moved the cache out
  of the gateway, but the same cache survives in EF's identity map.
- `ManagementGateway.cs:541-548` makes the same claim.

## 4. Fix

**Minimal, correct, and where it belongs:** make `AlvoIdentityUserStore`'s reads untracked. The
port's contract is persisted membership, and this store is the component that breaks it for any
long-lived scope, whether a Blazor circuit, a background service or a future host. Fixing it in the
store rather than in the Admin/Host resolver keeps the invariant in one place (principle 2: a guard
in one adapter is optional by construction).

```csharp
// AlvoIdentityUserStore.FindAsync (and FindByEmailAsync, same shape, on NormalizedEmail)
var row = await store.Users.AsNoTracking()
    .FirstOrDefaultAsync(u => u.Id == user.Value, cancellationToken).ConfigureAwait(false);
return await ProjectAsync(row).ConfigureAwait(false);
```

`GetRolesAsync` works on an untracked row: it queries by `user.Id`. Verified with
`dotnet run -- --fixed`, which flips the "after disable" line to `False`.

`FindByEmailAsync` has the same staleness. It should normalise through
`users.NormalizeEmail(email)` and match `NormalizedEmail`, which keeps the case-insensitive test
green.

**Optional second layer:** have `AlvoAdminCallerResolver` resolve the keyed resolver from a fresh
`IServiceScopeFactory` scope for each call. This is defence in depth for a host-supplied
`IAlvoUserStore` with the same flaw. It is not a substitute for the store fix. Without it, the
`IAlvoUserStore` port contract should state "reads reflect the store as of the call, never a
per-scope cache", so that a third-party store knows its obligation.

**Security-core checklist items this touches:** authorization fail-closed, tenancy isolation, and
revocation. Mark it `needs-deep-review` and pair it with a `/security-review` run.

## 5. Tests to pin it

1. **Integration (ring1/2), `test/MMLib.Alvo.Identity.Tests`:** a new fact with **two scopes** over
   one SQLite file. Resolve through the keyed `IAlvoContextResolver` in a long-lived scope A. Call
   `SetDisabledAsync(id, true)` and `SetTenantAsync(id, t)` through
   `GetRequiredKeyedService<IAlvoUserAdministration>(UnguardedKey)` in scope B. Resolve again in
   scope A: the result must be `null`. In a variant without the disable, it must carry tenant `t`.
   This fails today and passes with the fix. Add a store-level twin: `IAlvoUserStore.FindAsync` in A
   sees `IsDisabled` written in B.
2. **E2E, `RevokedSessionScenarios`:** add an open-circuit scenario. Stage an edit and reach
   Preview. Disable the operator from `world.Services` (UnguardedKey). Click Apply **without**
   `GoAsync`. Assert the refusal and that the revision is still 1. Its fixture must be isolated,
   like the existing one. Targeting a non-bootstrap admin would show that the defect is not tied to
   the bootstrap admin, but it needs a second signed-in account. The bootstrap target is acceptable
   because the fixture is already isolated.
