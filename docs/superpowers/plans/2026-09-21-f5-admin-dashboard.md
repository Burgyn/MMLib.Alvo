# F5 admin dashboard — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the real admin dashboard — a Blazor application inside `MMLib.Alvo.Admin`, mounted
by `MMLib.Alvo.Host`, that an operator signs into and uses to define a backend, browse and change
records, read and edit the descriptor, simulate a policy, administer people and roll a revision
back.

**Architecture:** One application service, two transports (design §1.2). The dashboard resolves
`IAlvoManagement` from DI in-process and holds **no** project reference to `MMLib.Alvo`; an
architecture test pins that. Screens are server-interactive Razor components over the design
system already in `src/MMLib.Alvo.Admin/wwwroot/alvo.css`. Nothing in the dashboard evaluates a
policy, scores a row or re-serialises a descriptor.

**Tech Stack:** .NET 10, Blazor Web App (server-interactive), `Microsoft.NET.Sdk.Razor`,
ASP.NET Core Identity (sign-in via a form post, not a circuit), xUnit + Microsoft.Testing.Platform,
`Microsoft.Playwright` + xUnit for E2E.

**Spec:** `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` — read §1.2, §2.2,
§2.7, §3.7, §4.1–4.5, §5 and §6 before any task. This plan argues from it and does not restate it.

**Issues:** #227 (shell) · #228 (read-only browser) · #229 (schema editor + descriptor bridge) ·
#230 (first-run wizard) · #231 (Playwright E2E). Plus the two ports §2.7 and §3.7 require, which
have no issue yet and are filed as part of §7's obligation.

## Global Constraints

- **`MMLib.Alvo.Admin` must never reference `MMLib.Alvo`.** Abstractions only. Arch test, ring1.
- **No client evaluates a stored row** (§2.2.1, acceptance criterion 4). No per-record
  allowed/refused badge exists at any width.
- **The editor mutates the stored JSON document**, never a projection of it (§6.3-3). A round trip
  through typed objects narrows every key the projection does not know about.
- **A refused feature gets no control, or an inert one carrying the refusal verbatim** (§4.1).
  `UnhonouredFeatures.EveryRefusal` is the only source of those sentences.
- **375 px, no horizontal scroll, both themes.** Acceptance criteria 1, 2, 5.
- **Errors land inline at the field they concern, with the `fix`, and stay.** Never a toast (§5.5).
- **Every empty state says what to do next.** Never "No data".
- **`.cs` and `.razor` files are UTF-8 **with BOM**, CRLF.** The pre-commit `dotnet format` check
  fails otherwise.
- **Never push to `main`.** Branch `f5/admin-dashboard`, stacked on `f5/admin-prototype-iteration`
  because the amended design is what this builds against.

---

## File structure

```
src/MMLib.Alvo.Abstractions/Identity/
  AlvoUser.cs                        + Tenant                        (§2.7)
  IAlvoUserAdministration.cs         new port, six members           (§3.7)

src/MMLib.Alvo/Management/
  UserAdministrationEndpoints.cs     six admin routes                (§3.7)
  Internal/GuardedUserAdministration.cs  the self-grant guard        (§3.7 U3)

src/MMLib.Alvo.Identity/Internal/
  AlvoIdentityUserAdministration.cs  the implementation              (§3.7)

src/MMLib.Alvo.Admin/
  AlvoAdmin.cs, AlvoAdminOptions.cs, AlvoAdmin*Extensions.cs         done
  Components/AdminApp.razor, AdminRoutes.razor
  Components/Layout/    AdminLayout, Sidebar, BottomNav, Sheet, CommandPalette, Icons
  Components/Shared/    Panel, Section, Badge, NotYet, Refused, Skeleton, EmptyState,
                        ErrorPanel, DiffView, ConfirmByName, CodeBlock
  Components/Pages/     SignIn, Overview, Schema, Entity, Data, Record, Rules, Access,
                        History, Integrations, Settings, NotYetPage, FirstRun
  Internal/             ManagementGateway (one scoped reader over IAlvoManagement),
                        WorkingCopy (the JSON document the editor mutates), JsonPointerDiff

src/MMLib.Alvo.Host/
  AlvoHost.cs                        cookie auth, antiforgery, static assets, MapAlvoAdmin
  Internal/AlvoAdminSignIn.cs        the sign-in/sign-out endpoints

test/MMLib.Alvo.Admin.Tests/         unit + arch + public-API approval
test/MMLib.Alvo.Admin.Tests.E2E/     Microsoft.Playwright + xUnit, real host, real database
```

---

## Task 1: The shell (#227)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/{AdminApp,AdminRoutes}.razor`,
  `Components/Layout/{AdminLayout,Sidebar,BottomNav,NavSheet,CommandPalette}.razor`,
  `Components/Layout/{AdminSection,Icons}.cs`, `Components/Pages/{Overview,SignIn}.razor`
- Create: `src/MMLib.Alvo.Admin/{AlvoAdmin,AlvoAdminOptions}.cs`,
  `AlvoAdmin{ServiceCollection,EndpointRouteBuilder}Extensions.cs`
- Modify: `src/MMLib.Alvo.Host/AlvoHost.cs`, `src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj`
- Test: `test/MMLib.Alvo.Admin.Tests/BoundaryArchitectureTests.cs`,
  `test/MMLib.Alvo.Admin.Tests/NavigationTests.cs`

**Interfaces:**
- Produces: `AlvoAdmin.BasePath` = `/admin`, `AlvoAdmin.SignInEndpoint`, `AlvoAdmin.SignOutEndpoint`,
  `AddAlvoAdmin(IServiceCollection, Action<AlvoAdminOptions>?)`, `MapAlvoAdmin(IEndpointRouteBuilder)`,
  `AdminNavigation.{Live,NotYet,Footer,Bar}`.

- [ ] **Step 1: the boundary test first** — assert `MMLib.Alvo.Admin`'s referenced assemblies
      contain `MMLib.Alvo.Abstractions` and do **not** contain `MMLib.Alvo`. Run it; it passes
      vacuously today, which is the point: it must keep passing after the dashboard has screens.
- [ ] **Step 2: the navigation fact** — assert `AdminNavigation.Bar` has exactly five entries and
      none of them is `NotYet`, because the phone bar is the place a navigation starts lying (§4.3).
- [ ] **Step 3: the shell components** — `AdminLayout` renders sidebar + header + content on wide,
      bottom bar + sheet under 720 px, from the classes already in `alvo.css`
      (`a-shell`, `a-sidebar`, `a-nav-item`, `a-bottomnav`, `a-header`, `a-main`).
- [ ] **Step 4: the command palette** — `⌘K` opens it over the `alvo:palette` event `alvo.js`
      already emits; `g`+letter jumps; `Esc` dismisses; focus moves into the input and returns on
      close.
- [ ] **Step 5: host wiring** — `AddAlvoAdmin()`, `UseAuthentication/UseAuthorization`,
      `UseAntiforgery()`, `MapStaticAssets()`, `MapAlvoAdmin()`; a `Alvo:Admin:Dashboard` section.
- [ ] **Step 6: sign-in** — cookie scheme, a statically rendered form posting to
      `AlvoAdmin.SignInEndpoint`, `SignInManager` in the host. Failure renders inline, at the field.
- [ ] **Step 7: ring0 + `dotnet format`, then commit.**

## Task 2: The Management gateway and Overview (#228)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs`, `Components/Pages/Overview.razor`
- Test: `test/MMLib.Alvo.Admin.Tests/OverviewTests.cs`

- [ ] **Step 1:** one scoped reader over `IAlvoManagement` that a component holds — schema,
      descriptor, revisions, capabilities and info, each fetched once per render pass, with the
      `ManagementForbiddenException` turned into the inline error panel rather than an exception page.
- [ ] **Step 2:** Overview renders entity and record counts, the applied revision, the data
      provider from `GET {m}/info` (**never** an engine name — §4.2), and the *declared and not
      running yet* panel as `capabilities.warned` ∩ the descriptor's own top-level keys.
- [ ] **Step 3:** a test that the *not yet* panel is empty for a descriptor that declares none of
      the five warned subsystems, and non-empty for one that declares one. ring0. Commit.

## Task 3: Schema — read (#228)

- [ ] Entity list with the field count and the tenancy/audit badges from `SchemaModel`.
- [ ] Entity detail with the six tabs the drawing fixed: Fields, Relationships, Rules, On write,
      Indexes, API. The API tab renders the routes the Data API already publishes — a rendering,
      not a second document (§4.2).
- [ ] The descriptor pane beside it, with the lines the selected field owns marked.
- [ ] Test: every field facet rendered is one `schema/project.schema.json` admits for that type.
      ring0. Commit.

## Task 4: Data — browse and change records (#228)

- [ ] Grid over `Virtualize`, keyset paging, hidden fields named as hidden and never fetched.
- [ ] A record form generated from the field types, with the read-only and hidden facets honoured.
- [ ] Create, update and delete through the **Data API under the operator's own credential** — not
      through the Management API, which has no data surface (§2.4, D4).
- [ ] The RLS surprise is stated where it bites: an empty page on `list` and a `404` on `get` are
      what a rule that excludes you looks like; neither is a `403`.
- [ ] Test: the dashboard and `/api` return the same rows for the same caller (§6.1). ring2. Commit.

## Task 5: Rules and the simulator (#228)

- [ ] The five operations as sentences plus the CEL they produce, read from the descriptor.
- [ ] The simulator posts `(Entity, Operation, Caller)` and renders the verdict — the four 403
      causes, the `USING` predicate, what failing it looks like per operation, and which fields
      drop out. **No record id, no per-row badge** (§2.2.1).
- [ ] Test: no element carrying a per-record verdict exists at any width. Playwright. Commit.

## Task 6: Schema editor, preview and apply (#229)

- [ ] `WorkingCopy` — the applied document, the working document, a JSON-pointer diff between
      them. The editor mutates the working **document**, never a projection (§6.3-3).
- [ ] Preview is `PUT …/descriptor?dryRun=true`: the migration plan, the guardrail verdict, and
      the destructive confirmation that requires typing the entity name.
- [ ] Apply sends `If-Match` with the revision it previewed against; `428` and `412` are rendered
      as themselves, with the fix.
- [ ] Import and export: export is `GET …/descriptor` byte for byte; import validates against the
      frozen schema's own patterns before anything renders it.
- [ ] Test: after any UI change, `GET descriptor` equals what the editor sent (§6.3-3). ring2. Commit.

## Task 7: Configuration history and rollback (#228)

- [ ] The revision list with author and reason, and a revision-against-revision diff through the
      same `DiffView` the editor uses.
- [ ] Rollback posts with `allowDestructive` explicit, never implied, behind the same
      type-the-name confirmation. Commit.

## Task 8: The two ports §2.7 and §3.7 require

- [ ] `AlvoUser.Tenant`, honoured by `TenantResolver` as a **confirmation** of the credential's own
      tenant; the no-request branch resolves to the user's own. Unit tests from §6.1. 
- [ ] `IAlvoUserAdministration` — six members, six `admin` routes, every member may refuse by name;
      `IssueCredentialTokenAsync` and `SetDisabledAsync` refuse the bootstrap administrator.
- [ ] The self-grant guard in the **core**, covering role *and* tenant, registered as a decorator
      under the public interface, pinned by a contract test in `MMLib.Alvo.Testing`.
- [ ] `access` leaves `UnhonouredSubsystems.All` (§3.6) if and only if it is now enforced. Commit.

## Task 9: Access, Integrations, Settings (#228)

- [ ] Access: membership through `IAlvoUserAdministration` (takes effect at once), the role
      catalogue and the three levels through the descriptor (wait for an apply) — and the screen
      says which is which (§4.5).
- [ ] Integrations: `webhooks` and `templates` rendered beside `capabilities.warned`, creation
      refused with the refusal verbatim.
- [ ] Settings: `GET {m}/info`; API keys read-only with D7's consequence stated; no danger zone,
      because `DeleteProject` has no route. Commit.

## Task 10: First run (#230)

- [ ] Sign in as the bootstrap administrator, grant yourself a tenant, create the first project —
      it **does not create an account**, because the bootstrap already exists (§3.5, §4.2). Commit.

## Task 11: The E2E suite (#231)

**This is the task the whole plan is judged on.** A real host, a real database, a real descriptor,
driven through the browser.

- [ ] `test/MMLib.Alvo.Admin.Tests.E2E` — `Microsoft.Playwright` + xUnit, starting
      `MMLib.Alvo.Host` over SQLite with a temp file, bootstrap administrator configured.
- [ ] Scenario: **first run** — sign in, land on Overview, see the field-service project.
- [ ] Scenario: **create a project from nothing** — an empty descriptor, add an entity, add fields
      of every type the schema admits, preview, apply, and read the descriptor back.
- [ ] Scenario: **change an evidence** — add a field to an existing entity, preview the plan, see
      the destructive step named, confirm by name, apply, and find the column serving records.
- [ ] Scenario: **records** — create, edit and delete a record through the dashboard, and verify it
      through `/api` with the same credential.
- [ ] Scenario: **policy** — simulate as three callers and assert the verdicts match what `/api`
      actually answers for the same context.
- [ ] Scenario: **rollback** — apply, roll back, and find the descriptor byte-identical to the
      earlier revision.
- [ ] Scenario: **a `Not yet` section opens and breaks nothing** (§6.2).
- [ ] Scenario: **375 px** — every one of the above at phone width, no horizontal scroll.
- [ ] Scenario: **keyboard only** — a full schema change without a mouse (§6.3-6).
- [ ] The console is asserted clean in every scenario.
- [ ] `scripts/test-admin-e2e`, in no ring, for `test-load`'s reason. Commit.

## Task 12: Close out

- [ ] ring0, ring1, ring2 green; `dotnet format` clean.
- [ ] Public-API baselines: every added `public` symbol justified against
      `alvo-architecture-rules`' *"public is the contract"*, or made `internal`.
- [ ] `docs/PLAN.md` ticked where it is now true, and nowhere else.
- [ ] `alvo-plan-guard` dispatched; findings answered, not noted.
- [ ] `alvo-pr-report`; PR opened against `main`. Nothing pushed to `main`.
