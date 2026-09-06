# PR-D — measured cost (implementation plan)

Design: `docs/superpowers/specs/2026-09-02-f4-pr-d-measured-cost-design.md`.
Branch: `f4/pr-d-measured-cost`. Issues: #126 (fix + close), #127 (fact + close),
#118 (measurement only — **left open**, design §8.1).

**The PR's claim is that it changes cost, not conduct.** The operational form:

> **No existing assertion is weakened, deleted, or re-baselined. Additions only. Every
> Verify baseline is byte-identical.**

*(Not "no existing file is touched" — that rule was wrong, and its only effect would be to
push a fact out of the file it belongs in. See design §6.)*

Revised after `alvo-plan-guard`; the four corrections that changed real steps are marked
**[guard]**.

---

## Slice 1 — #118: pin the resolve counts (no production change)

Pure measurement. First, because it establishes the decorator pattern slice 3 reuses and
proves the premise correction before any code moves.

1. `test/_shared/api/CountingPolicyEngine.cs` — decorator over `IPolicyEngine`, forwards
   `Resolve` verbatim, records `(entity, operation)` per call under a lock. Modelled on
   `RecordingContextAccessor` (`AlvoApiWorld.cs:621`), the in-repo precedent.
2. ~~Registered through the existing `AlvoApiWorldSetup.ConfigureServices` hook.~~
   **[corrected during implementation]** That cannot work, for the reason this plan already gives
   in slice 3: `ConfigureServices` runs *before* `AddAlvo`, and every Alvo registration is a
   `TryAdd` — so a decorator registered there wins the slot, `PolicyEngine` is never registered at
   all, and the decorator has nothing to wrap. Decoration has to run *after* the registration it
   decorates. The implementation therefore adds a `ConfigureServicesAfterAlvo` hook plus a
   `ServiceDecoration.Decorate` helper, which removes the existing `ServiceDescriptor` and
   re-registers a factory wrapping whatever that descriptor described. Still no production DI
   change. **`IPolicyEngine` has no forwarding registrations** — unlike `IPolicyCatalogProvider`,
   see slice 3 — so a single decorator is safe here.
3. New `test/MMLib.Alvo.Api.Tests/PolicyResolutionCountTests.cs`:
   - `A_list_resolves_the_policy_exactly_twice` — the HTTP gate plus the port's authority.
   - `A_read_by_id_resolves_the_policy_exactly_once`.
     **[guard]** Doc must state this number is *expected to move*:
     `DataApiEndpoints.cs:152-166` tells a future author to add the guard back the moment
     the delegate interprets caller input, naming `select` (#117/PR-D2) and `If-Match`.
     The fact exists so that becomes a deliberate edit, **not** to forbid it.
   - `A_create_an_update_and_a_delete_each_resolve_the_policy_exactly_twice`.
     **[guard]** Scope to the **non-replay** path. A keyed-create *replay* resolves `get`
     a third time (`EfAlvoData.cs:596`, `InMemoryAlvoData.cs:223`) and that resolve closed
     a row-level authorization bypass. The doc names it as correct, so the fact can never
     be read as an argument for deleting it.
   - `A_keyed_create_replay_resolves_the_policy_three_times` — the counterweight that
     pins the security control rather than leaving it merely un-broken.
4. Class doc carries design §3.2 **as revised**: the decline rests on the measured cost
   (one `PolicyDecision` allocation), and explicitly records that a catalog-reference-keyed
   cache *would* be safe — it is simply not worth it. **[guard]** Do not ship the withdrawn
   "stale by construction" argument.
5. Failure messages list the recorded `(entity, operation)` pairs. A bare `ShouldBe(2)`
   failure is unactionable.

**Verify:** ring0; all facts green on unmodified production code. Then temporarily add a
third `Resolve` to `MapList`, confirm the list fact fails, revert.

## Slice 2 — #127: pin the attempt count (no production change)

1. **Append** a fourth fact to
   `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteIdempotentCreateFailureTests.cs`; the three
   existing facts are untouched:
   `A_unique_violation_in_the_callers_own_data_costs_one_write_attempt`.
2. `host.ClearStatements()` before the act, count matching entity inserts after.
   `SqliteAlvoDataFixture` already exposes `Statements`/`ClearStatements()` via
   `SqlCapture`; no new infrastructure.
3. Assert **exactly 1**; the pre-#138 build gives 10. The doc states why the *count* is
   asserted and not the outcome: all three existing facts pass on a build that retries ten
   times and then throws the same exception.
4. Guard against a vacuous 0 — assert first that a *successful* create records exactly one
   entity insert, so the matcher is proven to match before it is used to assert a bound.
5. Match on the statement's shape, not a substring that could also match the idempotency
   table's insert. Those are hand-built `DbCommand`s that never reach EF's
   `CommandExecuting` and so are invisible to `SqlCapture` — but the matcher must not
   depend on that accident.
6. **[guard]** The doc states the **SQLite-only** scope (design §4.3): the count is a
   function of `IAlvoSqlDialect.DecodeConstraintViolation`, `TSqlSqlDialect` answers `null`
   and would still burn ten, and the PostgreSQL leg belongs to **#139**.

**Verify:** ring0. Then locally remove the `ConstraintViolationTranslator` wrapper in
`EfAlvoData.InsertAsync`, confirm the count goes to 10 and the fact fails, revert.

## Slice 3 — #126: the fix, measurement first

**TDD order — counting facts before the hoist.**

1. **[guard] One decorator, not two.** `Rules/Setup.cs:45-49` registers `ISchemaRegistry`
   and `IRoleCatalogProvider` as *forwarding* registrations to the single
   `IPolicyCatalogProvider`, and documents why two independently primed holders are a
   defect. Registering `CountingSchemaRegistry` and `CountingPolicyCatalogProvider`
   separately would no-op all three `TryAdd`s and could leave the transformer reading a
   second, unprimed holder — the fact would then die on the framework-invariant throw, or
   pass for the wrong reason.

   Correct shape: **one** `CountingPolicyCatalogProvider` implementing
   `IPolicyCatalogProvider` (which already inherits `ISchemaRegistry` and
   `IRoleCatalogProvider`), registered once and **forwarded** for the other two, mirroring
   the production registration exactly.
2. **[guard]** It must forward `GetSchema()` to the **inner** provider.
   `PolicyCatalogProvider.GetSchema()` is `Current?.Schema ?? _unprimedSchema` — a
   decorator implementing it via its own `Current` double-counts and entangles the two
   counters.
3. New `test/MMLib.Alvo.Api.Tests/OpenApiDocumentCostTests.cs`. Seed **several** entities —
   at N=1 an O(N²) scan and an O(1) lookup are indistinguishable. Assert per
   `/openapi/v1.json` request:
   - `GetSchema()` called **once**;
   - `Current` read **once** (design §6 — both land at 1, not 1 and N);
   - counterweight: the document still carries every entity's tag, schema component and
     five operations, so the fact cannot be satisfied by a transformer that stopped
     working.

   These fail on today's code (6N and 6N).
4. Then `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs`:
   - Build one `Dictionary<string, EntityView>` in `TransformAsync` from a **single**
     `GetSchema()` and a **single** `Current` read; `EntityView` is a private readonly
     record struct of `(EntitySchema Schema, IReadOnlySet<string> Hidden, IReadOnlySet<string> ReadOnly)`.
   - `Describe` and `Enrich` take the view; `EntityOf`/`FlagsOf` collapse into the builder.
   - **Keep both throws, with their message text unchanged.** Absence stays a loud
     framework invariant; nothing asserting on those messages moves.
   - Extract per `alvo-dotnet-conventions` (~25-line ceiling).
5. `OpenApiDocumentTests` and every Verify baseline must be **byte-identical** — that is
   this slice's no-behaviour-change proof. If a baseline moves, the Stop hook demands
   `alvo-snapshot-judge`; treat that as a signal the hoist changed output, to be
   investigated, never re-baselined.

**Verify:** ring0, then ring1.

## Slice 4 — docs and the record

1. `docs/architecture/data-api.md` — rewrite the stale ten-transactions paragraph
   (`:735-742`) per design §4.2/§4.3: the caller-constraint path leaves on the first
   attempt; the idempotency PK race and an unrecognised `DbException` legitimately still
   retry; a dialect answering `null` still burns ten, so this is a per-dialect property;
   the count is pinned on SQLite and PostgreSQL is #139's. Make it consistent with
   `:643-647`.
2. `docs/architecture/data-api.md:177-178` — leave the #117 note; still true, PR-D2 owns it.
3. `CHANGELOG.md` — the #126 entry; correct the "the rest of #127 is still open" line to
   what §4.2 says actually remains.
4. **[guard] `.github/workflows/mutation.yml` is NOT touched** (design §7). `suite:` is a
   one-sided 60 % floor against shrinkage, documented in `scripts/assert-mutation-run` as
   a dated snapshot that growth must not fail. Post the measurement (91.7 min of 120;
   1062 tests vs a floor of 514) as a comment on **#143**, which owns the re-shard.

## Slice 5 — gates

1. `scripts/test-ring0` → `test-ring1` → `test-ring2`.
2. `dotnet format`; CRLF + UTF-8 BOM normalisation on every changed `.cs` (CLAUDE.md
   gotcha). Do **not** normalise `.csx`/`.http` under `test/teapie*`.
3. **[guard] `needs-deep-review` label**, plus the `alvo-security-core-review` checklist —
   the guard flagged this yes: the PR counts `IPolicyEngine.Resolve`, hoists reads of the
   compiled catalog's `hidden`/`readOnly` masks on an anonymous-reachable endpoint, sits on
   the data port's retry path, and makes an explicit judgement about the fail-closed
   window.
4. Dispatch `csharp-reviewer` + the `alvo-security-core-review` skill as the substitutes
   for `/code-review` and `/security-review`; label them as substitutes in the PR.
5. Re-dispatch `alvo-plan-guard` on the finished branch.
6. `alvo-pr-report`, then `gh pr create`.
7. **[guard]** PR body: `Closes #126` and `Closes #127` on **separate lines** (a
   comma-separated list closes only the first). **No `Closes #118`** — it is left open for
   the maintainer to ratify the decline (design §8.1). Verify both closures after merge.
8. `docs/PLAN.md` marker stays on **F4** — 26 issues remain open in the milestone.

## Out of scope, stated in the PR

- #117 → PR-D2 (design §5 carries its constraints, including the `hiddenFields` trap).
- No policy-decision cache (§3.2). No narrowing of the retry catch (§4.2).
- No `mutation.yml` edit (§7).
- The `#125` follow-up (`ApiKeyHash` → #36 KDF note) rides with PR-E.

## Risks

| Risk | Handling |
|---|---|
| A Verify/OpenAPI baseline moves in slice 3 | It must not. Investigate, never re-baseline; the Stop hook enforces a judge. |
| #126 fact passes vacuously at N=1 | Seed several entities; assert document content as the counterweight. |
| Slice 2 asserts 0 because `SqlCapture` missed the insert | Prove the matcher on a successful create first. |
| The counting decorator splits the catalog identity | One decorator over `IPolicyCatalogProvider`, forwarded for the other two (slice 3.1). |
| Slice 1's create fact collides with the replay resolve | Scoped to the non-replay path; the replay's three resolves get their own fact. |
