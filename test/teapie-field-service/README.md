# test/teapie-field-service — does the product behave as documented?

The end-to-end suite over `examples/field-service`, driven against the container
`docker-compose.field-service.yml` starts on `:8081`.

`test/teapie` is the sibling suite and it answers a different question. It runs against
`examples/vehicle-registry` and proves the **plumbing**: the image builds, the descriptor applies, the
routes exist, a row reaches PostgreSQL. It is the smoke path and it guards two mutations (swap the
descriptor, swap the provider). This suite proves the **product**: every feature F3 built, measured
against a real container rather than in process.

A separate suite rather than more folders in `test/teapie`, for a mechanical reason: the two run
against different stacks, different descriptors and different key sets, and TeaPie runs one
collection per invocation. Both read one generated environment file, so nothing is stated twice.

## Run it

```bash
scripts/test-e2e          # both stacks, both suites, plus three PostgreSQL row-level assertions
```

To iterate on one group, start the stack yourself (see `examples/field-service/README.md`), write the
environment file the way `scripts/test-e2e` does, and point TeaPie at a folder:

```bash
dotnet tool run teapie -- test test/teapie-field-service/020-Query \
  -e compose --env-file artifacts/teapie/env.json --no-cache-vars --no-logo
```

`env.example.json` shows the environment's shape. **The suite owns the database**: it seeds fixed
`reference` values that the descriptor declares unique, and it asserts exact row sets, so it needs a
freshly torn-down stack. `scripts/test-e2e` guarantees that; a second run against a live stack will
not.

## The groups

| Group | Cases | What it measures |
|---|---|---|
| `010-Seed` | 4 | Creates the working set through the API, by the key each rule admits. Asserts the audit stamps, the field types' round trip, that a null is present-and-null, and that both hidden fields are absent from the create's own echo. |
| `020-Query` | 3 | Every operator on the allow-list, `or=(…)`, `and=(…)`, `not.`, `order`, `select`, and keyset paging over four pages. |
| `030-Problems` | 2 | Nine violations in one refusal, by pointer and code, with the caller's own text never echoed back — plus the `unique`-violation defect below. |
| `040-Concurrency` | 2 | `ETag`/`If-Match` on an audited entity, and its refusal on an unaudited one. |
| `050-Idempotency` | 1 | A replayed create by **row count**, a 409 for the same key with a different body, and a new row for a different key. |
| `060-Confidentiality` | 1 | A hidden field is absent everywhere, and a filter over it is refused **byte-identically** to one over an undeclared field. |
| `070-Authorization` | 1 | The three shapes — 403, 200-with-an-empty-page, 404 — in one system state. |
| `080-Tenancy` | 1 | Two keys differing only in tenant; B cannot see, read, modify or infer A's rows. |
| `090-Docs` | 1 | `/openapi/v1.json` publishes the declared shape and obeys the same confidentiality rule. |
| `100-Scenarios` | 6 | Multi-step journeys that end by asserting the state of the world. |

## The bar every case meets

**A case that cannot fail for the reason its name claims is worse than no case.** Three rules follow
from that, and they are visible in every file:

1. **Assert the parsed shape, never a substring of the body.** `_shared/Rows.csx` exists for this.
   `Contains("\"items\":[]")` passes on a body that also carries a row the caller should not see.
2. **Assert *which* rows, not that the request succeeded.** Every filter case names the exact set of
   references it expects.
3. **Carry a control wherever the claim is satisfiable by the wrong behaviour.** `like` is paired
   with the same pattern in the wrong case; `and=(…)` with its left term alone; the empty page with a
   caller who *can* see the rows; the 404 with the same id read by a caller who owns it; the paging
   walk with the single-request read of the same query.

## Two defects this suite found, and pins

Both are the same class — **a database constraint the framework does not map onto `IAlvoData`'s
refusal families** — and both are answered `500` where a `409` belongs. Each case asserts today's
behaviour, labelled, so it turns red the day the defect is fixed.

- `030-Problems/002` — a duplicate value on a `unique` field. The framework validates `required`,
  `maxLength`, `enum`, `format`, `precision`/`scale` and `ref` existence; `unique` is enforced only
  by the database. An agent gets no violation, no pointer and no field name. On a tenant-scoped
  entity a globally-unique field also becomes a cross-tenant existence oracle: tenant B distinguishes
  `500` from `201` and learns whether tenant A holds a value.
- `100-Scenarios/001` — deleting a row a `ref` with `onDelete: restrict` still points at.

Both cases also pin what must stay true meanwhile: the 500 leaks no exception type, SQL, constraint
name or stack frame.

## Three TeaPie behaviours worth knowing before editing

Each cost a debugging round here.

1. **A script's top-level body runs *before* the requests.** Only `tp.Test` bodies are deferred, so
   `tp.Responses` is empty at the top level. Every case that hands ids to a later case therefore
   opens with a `Capture:` test that asserts nothing — a `SetVariable` inside a *failing* test never
   runs, and would take out every later case with "Variable 'X' was not found" instead of reporting
   the one real failure.
2. **A GUID-shaped environment value is typed as `Guid`**, so `GetVariable<string>` answers `null`
   for it. Use `Constant(name)` from `_shared/Rows.csx`.
3. **A request without a `###` separator is merged into its predecessor**, silently — the `# @name`
   line alone is not enough, and the symptom is one request too few and a puzzling 422.

Also: a request-variable JSONPath resolves object properties (`{{Name.response.body.$.id}}`) and
headers (`{{Name.response.headers.ETag}}`), but **not array indexing** — chain through a captured
variable instead.
