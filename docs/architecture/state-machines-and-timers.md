# State machines and declarative timers

How Alvo enforces an entity's `stateMachine` and arms its `timers` (HX-18; spec
`docs/superpowers/specs/2026-10-14-host-extensibility-design.md` §7 *HX-18* and its *HX-18 settled details*; plan
`docs/superpowers/plans/2026-11-06-hx-18-state-machine-timers.md`). Both are descriptor members compiled into the
policy catalog beside the before-hooks (`EntityPolicy.Lifecycle`), so a write judges them against the same catalog
revision as the rules that admitted it. Firing is HX-13's `TimerPump`, unchanged (*time.md*).

Prior art: django-fsm's `@transition(field, source, target, conditions, permission)` — named transitions over one field,
`'*'` for any source — is the shape adopted; XState's event-keyed machines were rejected (this is a guard over writes,
not an interpreter); PostgreSQL RLS `WITH CHECK` is the model for `when` (a post-image predicate judged in the write);
durable timers (Temporal, Durable Functions) and the transactional outbox for the timer row living in the write's own
transaction.

## Where each check runs

```
 ├─ BEGIN (write unit)
 │   ├─ 3  declarative before-hooks            IBeforeHookRunner.Run
 │   ├─ 3a STATE MACHINE over candidate ⊕ declarative patch       IEntityLifecycle.EnsureTransition
 │   ├─ 4  BEFORE-WRITE hooks (host)            — a Set of the state field fails invalid-set
 │   ├─ 5  WITH CHECK + tenant scope + lookup checks over the final candidate
 │   │     5a STATE MACHINE again over Merge(preImage, hostVetted)  (update always; create when a host patched)
 │   ├─ 6  write + rollups
 │   ├─ 7  AFTER-WRITE hooks
 │   ├─ 8  emit: outbox append                  EfAlvoData.EmitAsync
 │   │     8a TIMERS: plan from the two images, upsert/cancel alvo_timers on the same connection and transaction
 │   └─ COMMIT
```

**Stage 3a** runs after the declarative `mutate`s, so a `beforeUpdate` hook that moves the state must take a transition
too, and before any host before-write hook, which therefore only ever sees a move the machine admits. Stage-2 validate
hooks run earlier, outside the transaction, and see the requested move unjudged.

**Stage 5a** exists because a host before-write may change a field a `when` reads (it may not set the state field
itself: `WriteExtensionRunner` refuses it as `invalid-set`). The move is judged again over what will be stored, as
`WITH CHECK` is. On an update it always runs, so a `when` — and any host function it calls — is evaluated twice per
update; on a create it runs only when a host patched, since otherwise the candidate is stage 3a's. The `invalid-set`
check reads the runner's current catalog rather than the write decision's; a raced apply can therefore at most let a
host set a field that a stage-5a re-check then still judges against the decision's machine.

**Stage 8a** runs beside the outbox append, after the event is built, so the armed timer resumes the writer's
correlation and is caused by the write's event.

## The internal port

`IEntityLifecycle` (Abstractions, `internal`, visible to the core and the EF driver) has two members: `EnsureTransition`
(stage 3a/5a) and `TimerChanges` (stage 8a). Both take the write's `PolicyDecision` and read the compiled lifecycle off
it (`PolicyDecision.Lifecycle`), never `catalog.Current`. It follows the `IBeforeHookRunner` precedent but is **not
replaceable by a host**: the guard is authorization-adjacent, and a host-supplied port could turn it off. `EfAlvoData`
requires it (`ThrowIfNull`); an allowed decision carrying no compiled lifecycle throws rather than skipping the guard,
so a future public way to build an allow decision must carry one. `InMemoryAlvoData` calls neither member — it runs no
hook pipeline — so transitions and timers are tested with `MMLib.Alvo.TestHost`.

## The guard's decision table

| Write | Result |
|---|---|
| Delete | no transition — always admitted by the machine |
| Update leaving the state unchanged (or a `PATCH` not naming it) | admitted |
| Create, `initial` declared, stored state ≠ `initial` | 422 `validation`, code `transition`, pointer `/<field>` |
| Create, no `initial` | admitted, any state |
| Update moving to a state no transition covers from the stored one | 422 `transition` |
| A covering transition whose `roles` the caller holds none of, and no other passes `roles` | 403 |
| Covering transitions pass `roles`, every `when` is false | 422 `transition` |
| A covering transition passes both `roles` and `when` | admitted (declaration order; the first passing one wins) |
| A `when` throws | the write is refused (never read as true) |

`from: ["*"]` matches any stored value, including null and a value outside the machine. The system caller holds
`admin` and gets no bypass. In a batch a transition refusal is the row's `AlvoRowRefusal` (code `transition`), a role
refusal the existing `forbidden` row; over HTTP a batch refused only by transitions answers 422, one with any policy
refusal 403. The 422 names neither the stored state nor the one sent.

## The arming table

`due(row) = when(row) ? at(row) : null`, both evaluated over the record's own fields at the write's instant.

| Write | Timer row |
|---|---|
| Create, due non-null | upsert |
| Create, due null | nothing |
| Update, `due(new) ≠ due(old)`, `due(new)` non-null | upsert (fresh generation — a claimed fire of the old row finds nothing) |
| Update, due unchanged | nothing |
| Update, `due(new)` null (inactive `when`, empty `at` field) | cancel by key |
| Delete (hard; soft delete is refused at apply in this build) | cancel by key |

**Key and scope.** The key is `alvo:<entity>:<timer>:<id>`; the scope is the row's `tenant_id` text, or `""` on a
global entity. `IAlvoScheduler` refuses a host key starting `alvo:` (`ITimerStore` itself does not — *time.md*).

**Written at the emit site, not through `ITimerStore.UpsertAsync`.** The store's unit-join would re-enter the unit the
write already holds, and a hook-free write has no unit; `TimerWrites` (shared with `EfCoreTimerStore`) issues the same
SQL on the write's `DbConnection` and `DbTransaction`. A rolled-back write leaves no timer.

**Lock order.** A write locks its row, then the timer row; `FireAsync` deletes the timer row, then appends to the
outbox. Neither takes the other's first lock second, so there is no cycle. A move racing a claimed fire gives the row a
fresh generation and clears its lease: the stale `FireAsync` deletes nothing and appends nothing, and the timer fires
once, at its new time.

**The event.** `event` (default `<entity>.<timer>`, `-` → `_`) published as `System(scope)` with the writer's
provenance; data `{entity, id, timer, at}` (`at` ISO-8601 UTC). Host handlers consume it; descriptor automation cannot
subscribe (`$defs/eventPattern` stays frozen).

**What a timer may read.** Bare fields of its own row, `now()` and durations. Refused at apply: `old.`/`new.`/
`changed()` (one write's comparison), `@user`/`@tenant` (one caller's identity), relation terms, and rollup fields — a
rollup is recomputed by a raw update with no emit, so nothing would re-plan the timer. Also refused: `timers` on an
entity with a cascading ref (a cascade delete never reaches the write path). Accepted and documented: `onDelete:
setNull` on a ref a timer reads changes the row outside the write path, so the timer keeps its old due until the row's
next write, or until the next re-plan of the entity (below).

## Re-planning existing rows (issue 483)

A write arms only the row it writes, so a timer added to an entity — or a changed `at`, `when` or `event` — would reach a
stored row only at its next due-changing write. Each entity that declares timers therefore gets one **framework data
migration**, synthesized from the catalog beside the descriptor actions (`TimerReplanBindings`) and started by HX-13's
`DataMigrationStarter` (*time.md*): no dashboard step, no host registration.

- **Id = definition.** `/entities/<entity>/timers#<hash>`, the hash over every timer's name, `at`, `when` and `event`. The
  starter starts an id once per database, so the first revision that carries a new or changed definition starts it and an
  apply that leaves the timers alone starts nothing. The run is one `migration` delivery of that subscriber (retried,
  recorded, dead-lettered like any other), on event `alvo.migration.timers.<entity>`.
- **One run spanning tenants** (`JobSpec.SpansTenants`). The re-plan reads every row below the policy layer — an
  always-true predicate, no tenant scope, unmasked, as a rollup recompute reads — and arms each timer in its row's own
  tenant, so it needs no tenant list and is never held for one.
- **Keyset-paged, one page per transaction** (`IEntityTimerReplanner`, internal, implemented by `EfAlvoData`): 100 rows by
  id, locked with the update pre-image lock, then the timers already armed for them read, then changed, on the same
  connection — the write path's lock order, so a racing write either waits or is waited for.
- **The rule** (`EntityTimerPlanner.Replan`), with the armed timer in place of the pre-image:

| `due(row)` | Armed timer | Change |
|---|---|---|
| null | none | nothing |
| null | any | cancel by key |
| non-null | at the same due | nothing |
| non-null | at another due | upsert (fresh generation) |
| non-null, still ahead | none | upsert |
| non-null, already passed | none | nothing |

  Re-running it changes nothing — no upsert, so no new generation. The last row is the one deliberate difference from a
  write: the re-plan cannot tell a timer that never was armed from one that already fired, and firing a reminder twice is
  the worse mistake. A row whose new due has already passed is therefore armed only by its next due-changing write.
- **Provenance.** An armed timer resumes the run's flow (`AlvoEventProvenance.Effective` of the system caller inside the
  delivery): its correlation and causation, and its depth, checked against the chain-depth limit.
- **The catalog it plans with is the current one**, not the start revision's.
- **Caveats.** A rollback to a definition that was already re-planned once starts nothing (its id is started); rows written
  in between keep the due the later definition gave them until their next write. `setNull` changes (M8) are covered only
  when a re-plan runs for another reason. A removed timer's armed rows are not cancelled by the re-plan.
  The run cannot be redelivered from the dashboard: like a descriptor action, its subscriber is not a catalog entry.

A transition `when` reading a field hidden from the caller makes 200-vs-422 an oracle on that field — the property
`WITH CHECK` already has; left to the stack-level security review.

## Decisions and their cost

1. **Named transitions** `{from, to, roles?, when?}` — a per-state `on` map would be a breaking schema change later.
2. **Checked at stage 3** after the declarative `mutate`s, before host before-write; a host `Set` of the state field is
   `invalid-set` — host code moves state only through a separate write.
3. **422 `transition` / 403 roles** via appended `AlvoConstraintKind.Transition` — an exhaustive `switch` gains a case.
4. **Any matching transition that passes both `roles` and `when` admits the move** — none.
5. **Unchanged state, deletes: no transition; a create stores `initial` when declared** — "only drafts may be deleted"
   stays a `rules.delete`.
6. **No system bypass** — one more transition (listing `admin`) for jobs.
7. **Descriptor members only, no `EntitySchema` member** — a provider sees the machine only through the internal port.
8. **Refused at apply** (non-enum/non-stored/hidden state field, values outside `values`, `"*"` beside others, `to`
   in its own `from`, `initial` ≠ `default`, undeclared role, dynamic entities) — dynamic entities wait for a follow-up.
9. **Key `alvo:<entity>:<timer>:<id>`, scope = `tenant_id` or `""`; `IAlvoScheduler` refuses `alvo:`** — a host key with
   that prefix is refused.
10. **Arming rule above** — one PK delete per timer per write; existing rows are armed by the framework's re-plan data
    migration when a revision first carries a new or changed timer definition (issue 483, above) — a lapsed unarmed due
    is not armed.
11. **Written at the emit site on the write's connection** — §7's "through the unit's `ITimerStore`" holds in
    substance, not in the call.
12. **`at` compiles in `Condition` as a `Timestamp` through the internal `IValueCompiler`** — a host-replaced
    `ICelCompiler` makes apply refuse `timers`.
13. **Fired event to host handlers only** — a descriptor-only project needs one handler to act on a timer.
14. **Batch rows: `transition` / `forbidden`** — none.
15. **Not an extension** (no `AlvoExtensionKind`/`Stage`, no catalog row; the dashboard's *States* tab and simulator
    show them) — the Lifecycle tab does not list them.
