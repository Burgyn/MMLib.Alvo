# Reads — narrowings, enrichers and virtual fields (HX-16)

> How a host's read extensions reach a read, where the narrowing sits in the statement, who is narrowed, how a
> failure is answered, and where a virtual field is refused. Spec 2026-10-14 §4.12, with its *HX-16 settled details*
> (the plan `docs/superpowers/plans/2026-11-06-hx-16-reads.md` *Decisions*, cited here as D1…D15). The user-facing page
> is `website/src/content/docs/extending/reads.mdx`. The statement this extends is [`data-path.md`](./data-path.md)'s
> *One statement, one `WHERE`*.

## The port

A read extension reaches the driver through one internal port, `IReadExtensionRunner`
(`src/MMLib.Alvo.Abstractions/Rules/IReadExtensionRunner.cs`), implemented in the core by `ReadExtensionRunner`
(`src/MMLib.Alvo/Reads/Internal/`) over the extension registry and binder HX-03…HX-09 built. The EF driver reaches it
through Abstractions' existing `InternalsVisibleTo`, as it reaches `IWriteExtensionRunner`. Per read the driver asks for
two things: `NarrowingAsync` — a `ReadNarrowing` (an `AlvoFilter` plus the ids it came from), or `null` — and
`BeginEnrichment` — an `IReadEnrichment` (`IAsyncDisposable`) every materialised row passes through, or `null`.

**Why a driver port and not an `IAlvoData` decorator (D1).** A decorator cannot AND into a `get` — it would have to turn
a get into a list under another rule; it would push the narrowing through the caller-filter limits and the hidden-field
guard, which judge caller input; and a transaction unit's reads bypass decorators. Only the driver composes the one
statement. `InMemoryAlvoData` runs no read extension (as it runs no unit) but refuses a write naming a virtual field
exactly as the driver does.

## Where the narrowing sits in the statement

`ReadStatementComposer` renders a narrowing as its **own** parenthesised `WHERE` term, under its own bind-parameter
family `alvo_n` (`PolicyParameterPrefix.Narrowing`), in this order:

```text
USING  ∧  tenant scope  ∧  narrowing  ∧  row id  ∧  caller filter  ∧  keyset anchor
```

It is never inside the caller's filter, so nothing a caller supplies reaches its nesting level, and the
`ReadStatementComposerTests.A_narrowing_is_its_own_term_after_the_tenant_scope` snapshot pins the string. The page, the
read of its keyset anchor and the opt-in count share it: a cursor whose anchor the narrowing now excludes answers an
empty page, never the first one (`ReadExtensionFacts.A_cursor_whose_anchor_the_narrowing_excludes_answers_an_empty_page`).
A write's echo and an idempotent replay are read back under the `get` decision **and** the narrowing (operation `Get`,
D3). A write's target rows and its pre-images are not narrowed (writes are not narrowed), and neither is the Data API's
reference probe (`RecordValidator.IsResolvableAsync`), which runs inside `AlvoFlow.EnterRulesOnlyRead()` so its 422
matches the port's rules-only reference check (D15).

The composer type-checks each column-bound narrowing value through the binder's own conversion, so a value the field
cannot hold is `invalid-filter` at compose time rather than a bind-time crash.

## Who is narrowed

Every caller whose `ActorKind` is not `AlvoActorKind.System` (D2, the `AlvoTransactionOptions` precedent) — inside a
`TransactAsync` unit too. An event handler running as the system and every `AsSystem()` session are not narrowed; a host
that hands Alvo a hand-built system context skips its own narrowing, by design.

## Fail closed

A registered narrowing or enricher that cannot run correctly fails the read; it never answers rows it did not shape
(D4). `HookFailedException.OfRead(ids, reason)` (`Stage = Read`) carries the ids, `ProblemResultFactory.HookFailedRead`
answers 500 `hook-failed` with `extensions` and `reason`, and the failure is logged with ids and exception type only
(`ReadFailures`, `ReadLog`), never the host's message.

| Reason | Raised by | When |
|---|---|---|
| `exception` | `ReadExtensionRunner`, `ReadEnrichment` | the extension threw, or disposing the instances Alvo built or the read's DI scope threw |
| `unbound` | `ReadExtensionRunner` | registered, but not bound for the current catalog |
| `translation` | `ReadExtensionRunner` | a typed predicate threw `AlvoTranslationException` |
| `invalid-filter` | `ReadExtensionRunner.Checked`, `ReadStatementComposer` | over the `AlvoFilter` limits; a missing or virtual field; a value the field cannot hold |
| `invalid-hide` | `RowShaping.Hide` | a framework column (`AlvoManagedColumns.For(entity)`, D7) |
| `invalid-set` | `RowShaping.SetVirtual` | a field that is not virtual, or a value outside its type and facets |

Inside a unit the failure also poisons it (`AlvoFlow.Unit?.Blame(failure)`, the write-hook precedent): a failed read is
a failed unit. A write's echo is enriched inside the write's own transaction, so an echo that fails rolls the write
back — no committed write answers 500. A refusal the enricher catches (`invalid-hide`, `invalid-set`) still fails the
read with its reason. A narrowing may name a field the caller's policy hides (D5) — it is host-authored, as a rule's `USING`
is — and the runner logs `NarrowingNamesHiddenField` (Warning, ids only) once per extension and field, at first use,
because the filter exists only at run time.

## A view, not a boundary

A narrowing is a **view**, not an authorization boundary (D3, D15). A write reaches a row the narrowing hides from its
caller's list; a `ref` resolves to one; a write's echo of such a row answers the id alone, as a `get`-excluded echo
does. What a caller must not read or write belongs in `rules`. Prior art: a PostgreSQL `RESTRICTIVE` RLS policy is
ANDed with the permissive ones and can only remove rows — a narrowing is exactly that, authored in C# — with one
deliberate deviation: RLS applies a restrictive `USING` to `UPDATE` and `DELETE` too, a narrowing does not.

## The enricher's three moves

Every materialised row — a page, a get, a write echo (single and batch, row by row), a replay — passes through
`EfAlvoData`'s `RowShaper` in this order:

1. **the mask** — the caller's `hidden` fields are already gone (the projection never read them);
2. **pre-fill** — every virtual field the caller may read and selected is added as `null` (D8: a stable key set, and
   `As<T>()` keeps working);
3. **the enrichers**, in registration order, each seeing the masked row only (D6). `Hide` removes a field; `SetVirtual`
   on a virtual field the row does not carry — hidden from this caller, or not selected — is a no-op, so an enricher can
   never resurrect a field.

An enrichment opens only when there is a row to shape, so an unbound enricher fails only reads that return rows. Class
extensions come from one `CreateAsyncScope()` per read, opened only when a class extension is registered on the entity;
a class enricher is built once per read, at its first row, and disposed with the enrichment — every instance Alvo built,
then the scope, each attempted even when an earlier one throws (D14). A class the host registered in its services is
resolved from that scope and not disposed by Alvo. `Enrich<T>.Row` materialises once, at first use: a move made after
that is not reflected in it.

## What is never enriched

Stored rows; event images; hook images (`Old`/`New`/`Saved`); an id-only echo; a delete and its replay, which read
nothing back. An enricher changes only what a caller is answered.

## Virtual fields

`"virtual": true` (`schema/project.schema.json` `$defs/field/properties/virtual`) maps to `FieldSchema.IsVirtual`. The
EF model maps no column for it (`DescriptorModelBuilder`), the projection never selects it (`ReadProjection`), and a
stored field turned virtual is a destructive `DropColumn`. Refused:

| Where | What | Site |
|---|---|---|
| apply | `computed`, `rollup`, `default`, `required`, `nullable: false`, `unique`, `index`, `validation`, `renamedFrom`, type `ref`, `indexes[].fields`, a rollup's `field`/`via`, `events.exclude`, a dynamic entity (D9) | `Descriptor/Internal/VirtualFieldCheck.cs` |
| apply | a declarative before-hook `mutate` target | `Rules/Internal/BeforeHookCompiler.cs` |
| every CEL slot | reading it, `changed()` included | `Expressions/Internal/CelTypeChecker.cs` |
| Data API write | 422 `virtual` at `/<field>`; a virtual field the policy hides answers `unknown-field` (I3a) | `Api/Internal/RecordValidator.cs`, `PayloadViolations.cs` |
| Data API query | 422 `virtual-field` at `filter`, `order` or `select` | `Api/Internal/QueryViolations.cs` and the parsers |
| OpenAPI | `readOnly` in the read schema; absent from request bodies and list parameters | `Api/Internal/SchemaComponentBuilder.cs`, `DataApiParameters.cs` |
| port write | `AlvoAuthorizationException`; a hidden one reads as undeclared (D13) | `Schema/VirtualFieldWrites.cs`, used by `WritePayloadGuard` and `InMemoryAlvoData` |
| port filter/sort/select | the undeclared-field refusal | `QueryFieldGuard` |
| typed binding | a non-nullable CLR property on a virtual field | `Data/Internal/ShapeMapping.cs` |

## Not done

- **No telemetry span per read** (D14): failures are logged with ids, `Hooks.AssertRan` counts no read, and the *Reads*
  lane carries no p95.
- **The simulator does not render a narrowing's filter** (D11): the filter is per caller, so the catalog's `When` is
  `null` for a narrowing — a deviation from §4.13's "narrowing shows its filter".
- **`InMemoryAlvoData` runs no read extension** (D1); an in-memory test sees no narrowing or enrichment.
