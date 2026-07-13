---
name: alvo-security-core-review
description: Use when changing the rule engine, tenancy, CEL compilation, or authorization — the security core. Runs a deep-review checklist and marks the change needs-deep-review.
---

# Alvo security-core review

Alvo's trust model rests on one premise: app-side rules are as safe as native
row-level security *because the only path to data is through the framework*.
The rule engine, tenancy, CEL compilation, and authorization are the code that
makes that premise true. A subtle regression here doesn't fail loudly — it
leaks tenant A's data to tenant B, or lets an in-memory filter hide behind a
policy that never actually ran. Treat any change touching these areas as
security-sensitive by default, and run every item below before calling the
change done.

Background: `docs/product/alvo-specifikacia.md` §1.2 (rule engine, hard
guarantees) and the hard-invariants section; `docs/design-brief.en.md`
"Hard invariants / contracts" and "Rule engine (the security core)".

## Checklist

- **SQL predicate — user input is never interpolated.** All values reach SQL
  through parameters, never string concatenation/interpolation, and the set
  of allowed operators is an explicit allow-list. A property test must prove
  this (e.g. fuzz arbitrary CEL input, including injection payloads, through
  the compiler and assert the emitted SQL text never contains the raw
  values). "It looked fine in the examples I tried" is not a proof; the
  property test is.
- **Authorization goes into the SQL `WHERE`, never an in-memory post-filter.**
  If a policy is enforced by fetching rows and then filtering them in
  application code, it is not enforced — it has already leaked whatever a
  monitoring tool, a debugger, a cache, or a bug in the filter step can see.
  Every query path must attach the compiled predicate before rows leave the
  database.
- **Fail-fast compile.** A condition referencing a nonexistent column, entity,
  or field must error at *save* time (when the rule/hook/policy is written),
  not at request time. Deferring the error to first use turns a typo into a
  production incident instead of a rejected save.
- **Cross-tenant isolation.** User A must never see, modify, or infer the
  existence of user B's data — and tenant X must never see tenant Y's data.
  Prove this with adversarial tests: at minimum a two-user test (same tenant,
  different row-level permissions) and a two-tenant test (different tenants,
  otherwise identical setup). The isolating predicate must be enforced
  **inside the data port**, not layered around it — a custom endpoint that
  reads via the data port automatically inherits isolation; one that bypasses
  the port does not, and that is the bug class this checklist exists to
  catch.
- **Default-deny.** Nothing is exposed without an explicit policy. A query
  issued without tenant/authorization context must fail, not silently return
  everything (or nothing looking like success). If a new code path can reach
  data without going through an explicit policy check, that's a finding
  regardless of whether today's policies happen to cover it.
- **Before-hooks are in-transaction, time-budgeted, network-forbidden.** This
  must be structurally enforced (the hook context/API must make a network
  call inexpressible, not merely discouraged by convention or code review).
  Verify a before-hook still runs inside the same transaction as the write it
  guards, respects its time budget, and has no path — direct or indirect via
  injected services — to issue a network call. After-hooks are the place for
  network calls; if a before-hook needs one, that's a sign the logic belongs
  on a different rung (see `alvo-architecture-rules`'s computed/rollup/hook
  ladder).

## Close every review with this

End the review by stating explicitly in your output: **mark this change
`needs-deep-review`.** Recommend adding the `needs-deep-review` label to the
PR. Do this regardless of whether the checklist above found issues — the
label signals "this touched the security core and needs a second set of
human eyes," not "this failed review." This ties in with the `alvo-plan-guard`
subagent, which also escalates changes touching the core/rule engine/tenancy
with the same `needs-deep-review` marker before a PR opens.
