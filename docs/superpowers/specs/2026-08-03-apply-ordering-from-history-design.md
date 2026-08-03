# Ordering the boot apply from the append-only history (#145)

Stacked on the startup-lifecycle design
(`2026-08-02-startup-lifecycle-and-config-dx-design.md`), whose deviations 53
(the `Apply` default and its three costs) and 57 (the destructive gate ahead of
the mode) this narrows. Deviation numbering continues that design's series,
which ends at **65**.

## The defect, as traced rather than as filed

`Apply` is the default startup mode, so a booting replica may rewrite the schema
of a database another replica just wrote. Nothing compares the two descriptors'
*generations*, so **which artifact wins is decided by a race** and the loser has
no good outcome. The destructive gate (deviation 57) catches the cases that
*discard* data; what escapes it is non-destructive backwards change.

`DestructiveScan.Classify` was re-traced for this design. Destructive:
`DropTableOperation`, `DropColumnOperation`, and an `AlterColumnOperation` that
narrows (nullable → not-null, a shrinking or newly-imposed length/precision/scale,
a changed CLR type). **Not** destructive: `CreateTableOperation`,
`RenameTableOperation`, `AddColumnOperation`, `RenameColumnOperation`, a widening
or neutral `AlterColumnOperation`, every index operation
(`CreateIndexOperation`/`DropIndexOperation`/`RenameIndexOperation`), every
foreign-key operation, and every primary-key / unique-constraint operation —
the last three families set no `IsDestructive` **in either direction**.

So the reachable hazards, and what the trace changed about each:

1. **Index / constraint oscillation — reachable, and the cleanest example.**
   Two descriptors differing only in an `indexes` entry (or a `unique` flag) plan
   `AddIndex` one way and `DropIndex` the other. Neither is destructive, so under
   `Apply` each pod applies its own on every boot and the schema flip-flops. No
   data loss, no refusal, no signal.
2. **Rename oscillation — reachable only in a narrower shape than #145 states.**
   The issue's version ("B renames `city` to `town`; A restarts and plans
   `RenameColumn` back") is **not** reachable, and this is a correction. A rename
   is a genuine `RenameColumnOperation` only when the descriptor *declares* it
   (`renamedFrom`, handled by `RenamePrePass`). A pod whose descriptor merely
   still says `city` produces an unmatched drop + add, EF's differ *guesses* a
   rename, and `RenameGuessSplitter` splits the guess back into
   `DropColumn` + `AddColumn` precisely so it cannot bypass the gate — so the way
   back is destructive and is refused. Oscillation therefore needs **both**
   descriptors to declare mutually inverse renames, which is what a rename shipped
   and then reverted *by an author who wrote the revert properly* looks like.
   Narrower than filed, still real, and still nothing that orders the two.
3. **The rollback crash-loop — reachable, and the one anybody meets.** Deviation
   53's cost (c). A forward deploy advances the applied snapshot with no operator
   decision; redeploying the previous artifact plans a `DropField`, the gate
   refuses it correctly, and every pod exits 78 in a crash loop. The refusal is
   right and the **diagnosis is wrong**: the operator is told "destructive change
   refused", not "you are running an older descriptor than the database".

What is **not** the defect, and was published as if it were: the
additive-vs-additive union. A adds `region`, B adds `city`; B's plan against A's
applied snapshot contains a `DropColumn` of `region`, which is destructive and is
refused in every mode. Measured by
`ConcurrentBootTests.Two_replicas_adding_different_fields_end_on_one_descriptors_schema_not_the_union`.

## The mechanism: A′, derived from the history

`IDescriptorVersionStore` is an **append-only** version history and
`ListAsync(project)` already returns it oldest-to-newest. So a booting replica
does not need a counter anybody maintains; it can ask the history where it stands:

> If my descriptor's canonical content appears in the history, and its **newest**
> occurrence is older than the current revision, I am an old pod and must not
> apply.

"Newest occurrence" is load-bearing rather than pedantic: a descriptor
re-applied later (history `X, Y, X`) appears at an older revision *and* at the
current one, and it is current.

Canonicalisation is `AlvoDescriptor.Serialize(AlvoDescriptor.Parse(json))`
compared ordinally — the comparison `RuntimeSchemaService.IsSameDescriptorContent`
already makes. It is **extracted** to `DescriptorContent` and shared rather than
copied: a second notion of "the same descriptor" is exactly the kind of drift that
makes two code paths disagree about identity. No hash format is invented; a hash
would buy a smaller comparison and cost a canonical-form-to-bytes contract that
has to survive a serializer change, which is not a trade worth making for an
O(N) boot-time read.

**No new port member.** That is the point of choosing A′: the ordering falls out
of state the store already writes.

### The declared `revision` becomes an override, in one direction only

`AlvoDescriptor.Revision` is parsed today and read by nothing;
`schema/project.schema.json` documents it as *"used for optimistic concurrency
during apply"*. It now is — as an **override that can only ever conclude "you are
older"**:

- the booting descriptor declares `revision` **and** the current stored
  descriptor declares one **and** mine is lower → older, refuse;
- anything else falls through to the history comparison.

It deliberately cannot conclude "I am newer" (that would let a bumped counter
wave an out-of-order descriptor past the history), and it deliberately does not
refuse "equal revision, different content" even though that is an authoring
error. Both restraints exist for the same reason: a descriptor carrying a
decorative `revision: 1` that nobody bumps must not have its ordinary dev loop
broken by a field the author never opted into. The override can only ever *add* a
refusal that the history would have missed, never create one for a static counter.

### An absent `revision` means unprotected-but-compatible

Recommended, and taken. The alternative — refuse to apply on any drift when
`revision` is absent — is safe and **breaks every descriptor that exists**:
`revision` is optional in the frozen schema, no descriptor in this repository
declares it, and a zero-config `docker run` plus one edit is the loop the `Apply`
default was ratified for. A′ does not need the counter, so absent means the
history comparison alone, which covers every case where the older artifact was
*ever applied by Alvo* — i.e. every rolling deploy and every rollback. What it
does not cover is an older descriptor that was never applied here at all, which
no counter nobody maintains would have covered either.

### Rejected: leader election / an apply lock (option B)

The maintainer's initial preference, and it does not close this issue. A lock
provides **mutual exclusion**; this is an **ordering** bug. Serialise two
replicas holding different descriptors and one still applies last — ping-pong
serialised rather than concurrent. It is still owed for a different problem:
`baas-analyza.md:819` requires a scheduled job to fire *"exactly once in a
3-instance deployment"*, and when that machinery lands the apply path should use
it, because it also covers changes that never went through a descriptor — which
A′ can never cover. **Not** an EF-style lock table: EF Core's SQLite migration
lock is a row with no timeout that survives a killed process, so an OOM-kill
mid-migration wedges every later boot.

## Where the gate sits

`SchemaStartupPolicy.Decide` gains a fourth parameter — the ordering verdict,
computed by the caller and passed in, so the policy stays a pure decision table.
The gate order becomes:

1. empty plan → `Unchanged`
2. `Skip` → refuse only the unverifiable state, else `Unchanged`
3. **out of order → `StandDown`**
4. destructive without `AllowDestructive` → `Refuse`
5. no applied snapshot → `Initialize`
6. `Apply` → `Apply`, otherwise `Refuse`

**Ahead of the destructive gate (3 before 4), which narrows deviation 57's
ordering without weakening it.** Both verdicts refuse the same boot; only the
message differs, and "you are running an older descriptor than the database
(revision 1 versus revision 2)" is the diagnosis for the rollback crash-loop that
"destructive change refused" has been failing to give. The destructive gate is
untouched for every plan that is *not* out of order, and
`AlvoBootService.RefuseToDiscardDataWhateverWasDecided` still re-checks it
immediately before the DDL.

**After the empty-plan check, which is what keeps an ordinary restart O(1).** The
history is read only by a boot that would otherwise *change* the schema, so the
common case — a restart over an unchanged descriptor — pays no history read at
all. This also scopes the gate honestly: it governs the **apply**, not the
**serve**. A pod whose descriptor is older but whose schema is identical to the
applied one (a rules-only revision appended by the runtime path) still serves;
adopting the database's current descriptor is a different problem and not this
issue's.

**After the `Skip` branch.** `Skip` never applies, so it cannot enter the race,
and its contract is that the schema is somebody else's business.

## What an out-of-order boot does: stand down, not crash

A new outcome, `SchemaStartupOutcome.StandDown`. The boot records the refusal on
`AlvoBootState` (phase `Failed`), logs it at `Critical`, primes **nothing**, and
**returns normally**. The process starts, `/health/live` answers 200,
`/health/ready` answers 503 with `Failed`, and an orchestrator drains the pod
instead of restart-looping it.

This is the second instance of **deviation 65**'s shape, for the same reason: a
failing liveness probe gets the container killed, which is the wrong response to
a condition no restart can fix. It is deliberately *not* extended to the other
refusals, and the two are different in kind:

- a destructive or `Verify` refusal is an **authoring or configuration** error.
  Nothing but a human changes the outcome, and the fastest feedback is a loud
  failure at deploy time. It keeps throwing, and keeps exiting 78.
- an out-of-order boot is a **position in a deployment**. The pod is not
  misconfigured, it is behind. That is precisely what readiness is for.

Safety does not rest on nobody routing to it: the policy catalog stays unprimed,
which denies every operation, and `ISchemaRegistry` reports an empty schema, so
the route table materialises empty. A pod that stood down can answer 404 and 403
and nothing else.

**It does not make a rollback bootable**, and the design says so rather than
letting a reader hope. Rolling back still needs `AllowDestructive` (accepting the
loss) or a migration job applying the older descriptor. What changes is that the
operator is told *which* problem they have. The escape hatch for "I really mean
this older descriptor" is to bump its `revision` — which changes its canonical
content, so it is no longer the artifact the history recorded — and then to clear
the destructive gate as before.

## Cost: `ListAsync` reads the whole history

Measured, not assumed. The read is O(N) in applied revisions and pays twice:
`EfCoreDescriptorVersionStore.ListAsync` deserializes each row's `schema_json`, and
the ordering check canonicalises each row's `descriptor_json`. A forward deploy —
the case that never matches — pays the full N; a byte-equality fast path against
the JSON the boot loaded short-circuits the canonicalisation when a previous boot
recorded the same file verbatim.

Measured on SQLite in Release, over an 8-entity ~5 KB descriptor, worst case (no
row matches, so every one is canonicalised):

| Applied revisions | History bytes | `ListAsync` | Canonicalise all N | Total per boot |
|---|---|---|---|---|
| 50 | 0.25 MB | 6.2 ms | 3.7 ms | ~10 ms |
| 250 | 1.3 MB | 30.1 ms | 21.3 ms | ~51 ms |
| 1000 | 5.1 MB | 176.4 ms | 127.6 ms | ~304 ms |

Linear in N, as expected, and **acceptable**: it is paid once per process start,
only on a boot that would change the schema, and N counts *applied* revisions for
one project — a number that grows per schema-changing deploy, not per request. A
project at 250 revisions pays 50 ms of a boot that is already running DDL.

**The narrower query is surfaced, not taken.** `SELECT revision, descriptor_json`
without the schema JSON, or a stored canonical digest compared instead of the
JSON, would cut both halves — and both are **port changes** to
`IDescriptorVersionStore`, so they are a design decision rather than an
implementation detail. Deferred, with the trigger named by the measurement above:
take it when a project's history read passes ~250 ms, i.e. somewhere around 800–1000
applied revisions.

## Facts

| Fact | What would otherwise be believed |
|---|---|
| `DescriptorHistoryOrderTests.A_descriptor_the_history_has_never_seen_is_a_forward_deploy_not_an_older_pod` | that the gate bricks every deploy |
| `…A_descriptor_recorded_at_an_older_revision_is_an_older_pod` | the mechanism works at all |
| `…A_descriptor_re_applied_since_is_current_not_older` | the newest occurrence is what counts |
| `…The_comparison_is_canonical_so_reformatting_a_descriptor_does_not_make_it_new` | that whitespace defeats it |
| `…A_lower_declared_revision_is_an_older_pod_even_when_the_history_has_not_seen_it` | the override exists |
| `…A_higher_declared_revision_does_not_wave_a_descriptor_the_history_calls_older_through` | the override is one-directional |
| `SchemaStartupDecisionTests.An_out_of_order_boot_stands_down_instead_of_refusing` | the outcome is distinct |
| `…An_out_of_order_boot_is_diagnosed_as_older_rather_than_as_destructive` | gate 3 really precedes gate 4 |
| `…An_out_of_order_verdict_does_not_stand_down_a_boot_with_nothing_to_apply` | the gate governs the apply |
| `AlvoBootServiceTests.A_boot_holding_an_older_descriptor_starts_not_ready_instead_of_crash_looping` | end-to-end, incl. the exit path |
| `…A_backwards_change_the_destructive_gate_cannot_see_is_stood_down_rather_than_oscillating` | that the gate closes something nothing else did |
| `ConcurrentBootTests.A_replica_holding_an_older_descriptor_stands_down_while_the_current_one_serves` | both engines, over a database that already holds a history |

`A_backwards_change_the_destructive_gate_cannot_see_is_stood_down_rather_than_oscillating`
is the one that matters most, because it is the only fact here that **before this
change went green by applying**: an index added one way and dropped the other is
destructive in neither direction, so removing the ordering gate records a third
revision — the oscillation itself — rather than merely losing a diagnostic.

The mutations run, and what each turned red:

| Mutation | Observed |
|---|---|
| the ordering gate never fires | 6 red — 3 decision-table, 2 host, 1 SQLite concurrency |
| the ordering gate moved *after* the destructive gate | 3 red, and exactly the three diagnosis facts; the index fact stays green, since its plan is not destructive |
| the history searched oldest-first | `A_descriptor_re_applied_since_is_current_not_older` |
| `declared >= applied` → `>` (equal counts as older) | `Equal_declared_revisions_with_different_content_are_not_treated_as_out_of_order` |
| `declared >= applied` → `==` (the comparison's direction) | `A_lower_declared_revision_is_an_older_pod_even_when_the_history_has_not_seen_it` |
| the canonical comparison dropped, leaving bytes only | `The_comparison_is_canonical_so_reformatting_a_descriptor_does_not_make_it_new` |
| `appliedAs >= current` → `>` | 2 red — the current descriptor and the re-applied one both read as older |
| standing down throws, like every other refusal | 3 red — both host facts and the SQLite one, on the exit path |
| the harness's `Serving` reduced to "it did not throw" | the SQLite fact — a replica that stood down would otherwise count as serving |

One thing deliberately has **no** discriminating mutation, said plainly rather than
implied: skipping the history read for an empty plan is a *cost* decision, and
removing the short-circuit changes no behaviour, because the policy's own empty-plan
branch returns `Unchanged` before the ordering gate either way.
`An_out_of_order_verdict_does_not_stand_down_a_boot_with_nothing_to_apply` pins that
branch, which is what makes the short-circuit safe to have.

## Deviations from the sources

Continuing the startup design's series, which ends at 65.

66. **The apply ordering is derived from the append-only history, not from the
    declared `revision`.** No source describes either; #145 offered the declared
    counter (A) and leader election (B). A′ is chosen because A makes correctness
    depend on a counter nothing enforces monotonic, and B provides mutual
    exclusion for an ordering bug. B stays owed for `baas-analyza.md:819`'s
    exactly-once cron requirement, where it is the right mechanism.
67. **An absent `revision` means unprotected-but-compatible.** Recorded as a
    decision rather than a default, because the safe alternative (refuse to apply
    on drift when no `revision` is declared) was genuinely on the table and is
    rejected for breaking every existing descriptor and the zero-config loop
    deviation 53 was ratified for.
68. **The declared `revision` can only conclude "you are older".** It cannot
    conclude "newer", and it does not refuse equal-revision-different-content
    even though that is an authoring error — so a decorative counter nobody
    maintains cannot create a refusal that did not exist before.
69. **The ordering gate is evaluated ahead of the destructive gate**, narrowing
    deviation 57's stated ordering. The same boots are refused; the more precise
    diagnosis wins when both apply. The destructive gate is unchanged for every
    plan that is not out of order, and is still re-checked immediately before the
    DDL.
70. **The gate governs the apply, not the serve.** It is not consulted for an
    empty plan (so an ordinary restart pays no O(N) history read) and it does not
    override `Skip`. The consequence is stated rather than hidden: a pod holding
    an older descriptor whose *schema* matches the applied one still serves its
    own older rules. Making a pod adopt the database's current descriptor is a
    different design.
71. **An out-of-order boot stands down instead of throwing** — it starts, primes
    nothing, publishes `Failed`, and answers 503 on readiness. Deviation 65's
    shape, applied a second time, and deliberately not extended to the
    destructive or `Verify` refusals, which stay hard stops with exit 78. #145's
    acceptance criterion asks for "never a crash loop"; the reason it is the
    right answer *here* and the wrong answer *there* is the difference between a
    deployment position and a configuration error.
72. **`IDescriptorVersionStore` becomes a required boot dependency**, widening
    the implicit provider contract the same way deviation 60 widened it for
    `IRuntimeSchemaWriter`. Both in-repo drivers already register it from the
    same instance that serves `IAppliedSchemaStore`; the cost is borne by a
    future third-party provider that implements only the single-row port. The
    boot keeps reading `IAppliedSchemaStore` for the current snapshot rather than
    taking `history[^1]`, so that stage 1's unconditional read and every probe
    count pinned on it stay exactly as measured.
73. **The whole history is read on a drifting boot, and the narrower port member
    is deferred.** Surfaced because it is a port change and therefore a design
    decision; the measured cost and the trigger for taking it are above.
