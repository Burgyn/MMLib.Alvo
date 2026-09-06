# Plan — F4 PR-B: nullable sort keys page, and a page can carry its total

Design: `docs/superpowers/specs/2026-08-30-f4-pr-b-nullable-sort-and-count-design.md`
Issues: **#116**, **#110**.

Two slices, in order. Slice 1 stands alone and is the riskier one (it changes a boundary
predicate); slice 2 builds on nothing from it. Ring0 after each step, ring1 after each slice,
ring2 before the PR.

---

## Slice 1 — #116: an `IS NULL`-aware keyset boundary

1. **Port.** Delete `AlvoQuery.EnsureSortKeysCanBePaged` and the `IsNullable`/`IsPaged` helpers it
   alone uses. Rewrite the `Sort` remarks: a nullable key is pageable, and the boundary honours
   `AlvoSort.Nulls`. Move the public-API baseline.
2. **Renderer (test first).** `KeysetSqlRendererTests` gains the four shapes × two directions as
   rendered-SQL facts, plus the non-nullable arm unchanged. Then `KeysetSqlRenderer.Level` branches
   on `declared.Nullable` and on whether the anchor's value is `null`.
3. **Drop the guard calls** in `EfAlvoData.QueryAsync`, `InMemoryAlvoData.QueryAsync` and
   `QueryStringParser.EnsureWithinPortRules`; delete `QueryViolations.UnpageableSortKey`.
4. **The lockstep fact** — new inherited `AlvoDataPagingTests` theory: walk a nullable-keyed set one
   row per page and assert the concatenation equals the unpaged sorted read, for
   `{asc,desc} × {nullsfirst,nullslast}`. Fixture: several nulls *and* duplicate non-null values.
5. **Rewrite the refusal suites** that now assert the opposite: `SqliteAlvoDataNullSortKeyTests`,
   `AlvoDataAdversarialTests.A_paged_read_sorted_by_a_nullable_field_is_refused_…`,
   `InMemoryAlvoDataTests`' two refusal facts, `QueryStringParserPropertyTests`' guard call,
   `SortSqlRendererTests`' "provably inert on a paged read" remark.
6. **HTTP.** `DataApiQueryTests` / `QueryStringParserTests`: `?order=<nullable>&limit=…` answers 200
   and the null placement is observable end to end. Update the teapie e2e that asserts
   `unpageable-sort-key` (`test/teapie-field-service/020-Query/002-*`).
7. **Docs.** `DataApiParameters.Order`, `DataApiDocumentation`'s list description,
   `docs/architecture/data-api.md` and `data-path.md`; OpenAPI snapshot moves.

## Slice 2 — #110: `Prefer: count=exact`

1. **Port.** `AlvoQuery.IncludeTotalCount`; `AlvoPage.TotalCount` remarks; `IAlvoData.QueryAsync`
   remarks (count is over the policy-filtered set, not the page; opt-in; a second statement).
   Baseline moves.
2. **Composer (test first).** `ReadStatementComposerTests`: the count statement carries the same
   `WHERE` terms, no `ORDER BY`, no window, no anchor, `COUNT(*)` projection. Then extract the
   shared term composition and add `ComposeCount`.
3. **Drivers.** `EfAlvoData`: when asked, execute the count through
   `Database.SqlQueryRaw<long>(…).ToListAsync()` with the same binder.
   `InMemoryAlvoData`: count the filtered set before paging.
4. **Inherited facts.** `AlvoDataPagingTests`: the count is of the whole filtered set, not the page,
   and it is `null` unless asked. `AlvoDataAdversarialTests`: the count respects the policy
   predicate and the tenant scope — a second tenant's rows are not counted.
   `AlvoDataStatementTests`: a counted list emits two statements and the second binds `alvo_u`.
5. **HTTP.** `PreferHeader` (RFC 7240 parse, new file) + `DataApiEndpoints` wiring:
   `Preference-Applied: count=exact`, `planned`/`estimated` degrade, unknown ignored.
   `DataApiPage.Count`.
6. **Published contract.** `DataApiParameters` `prefer`, `DataApiHeaders` `Preference-Applied`,
   `SchemaComponentBuilder` `count` in the envelope + `required`; snapshot moves.
7. **Docs.** `docs/architecture/data-api.md` gains a count section incl. the non-atomicity
   deviation; `CHANGELOG.md`.

## Gates

ring0 per step · ring1 per slice · ring2 before the PR · `alvo-plan-guard` ·
review subagent (substituting for `/code-review` + `/security-review`, both user-only) ·
`alvo-pr-report` · PR with `Closes #116` and `Closes #110` on separate lines.
