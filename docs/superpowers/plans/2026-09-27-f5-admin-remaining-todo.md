# F5 admin — every remaining todo-admin item, in nine batches

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** close every open item of `docs/todo-admin.md` in PR #264 (the maintainer's instruction: "don't forget the rest, everything must be finished"), verified against the code first.

**Spec:** the triage `docs/superpowers/specs/evidence/2026-09-27-todo-admin-triage.md` — §1 per item (status at HEAD with file:line, acceptance, size, scope), §2 dependencies, §3 batches. It is the requirements for each task below; each task's brief is its §3 batch plus the §1 entries of the items it names. The dashboard's binding interaction language is §3 of `docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md`.

**Rulings (controller, 27 Sep 2026):**
1. All nine batches land in #264 (the maintainer asked for everything in this PR); order B1 → B2 → B3 → B4 → B9 → B5 → B7 → B6 → B8.
2. Items the triage files on F6 (37 identity expected-version, 38 SQLite lock investigation, 43 two-replica first boot, 46(ii) lockout redesign) become GitHub issues with the todo's evidence, linked from the todo ("→ #NNN (F6)"); #272 and #273 stay where they are. Filing happens with the release step.
3. Where a key's subsystem is not built (storage: dynamic → #41, auth.providers → #36, realtime → #38, automation webhooks → #33/#120), "done" is a precise notice in the dashboard + a core warning where the triage says so, never a half-built subsystem.
4. Abstractions grows only in B7 and B8, each symbol justified (alvo-architecture-rules); OpenAPI / e2e pins move once per batch.

5. **Narrowed by the maintainer (27 Sep): only what quality day-to-day use of the admin needs; everything else becomes a separate issue.** Kept: B1 (#267 read, #269 rest, field description display, 42); B2 (21 notice, 27 honest notices; editors stay in #271); B4 (25, 44); B7 reduced to 31 (+ the ManagementWarnedBlock.Block / ManagementCapabilities.Warned doc fixes carried from B2); B8 reduced to 39 + an operator Unlock (46 part i). Deferred to issues: 26 → #276, 29 → #277, 32 → #278, 36 → #279, 40 → #280, 41 → #281, 45 (B9) → #282, 37 → #283, 38 → #284, 43 → #285, 46(ii) → #286; kept open with a scope comment: #265, #267 edit half, #268 identity/editors, #270 (B6), #271 editors. Tasks 5, 6, 8 below are therefore dropped; Task 7 and 9 are reduced as stated.

## Global Constraints

- Security core (B6, B8, and B9's returnUrl): `.claude/skills/alvo-security-core-review`; default-deny; `needs-deep-review`; the maintainer's `/security-review` before the PR is marked ready.
- Public API: Admin/Identity/Host/core baselines may grow only by what the task names; Abstractions only in B7/B8; every added symbol justified.
- `.cs` BOM + CRLF; `.razor` BOM + LF; methods ≤ ~25 lines; XML docs; comments say why.
- E2E: role / label / test-id selectors only (EndToEndSelectorTests at 0); pattern scans (PatternLanguageTests, FieldConventionTests, ConfirmFocusScenarios) stay green and cover new controls.
- Each item closed gets its ✅ Done line in docs/todo-admin.md naming the commits; each linked GitHub issue is left for the release step to close/comment.
- Gates per task: the touched projects' tests, `scripts/test-ring1`, `scripts/test-admin-e2e` whole, `dotnet build -c Release` of touched projects; ring2 for B2/B3 (core).
- Conventional Commits ending with `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`.

---

### Task 1: B1 — The schema screen shows what the descriptor declares
Triage §3 B1 (items #267 read half, #269, #268, 42, 29). - [ ] implement, test, commit per sub-item.

### Task 2: B2 — The capability surface is honest
Triage §3 B2 (items 21, 27, #271). - [ ] implement, test, commit per sub-item.

### Task 3: B3 — Field-editor facets that change the database
Triage §3 B3 (items #265, 41). - [ ] implement, test, commit per sub-item.

### Task 4: B4 — Data grid correctness
Triage §3 B4 (items 25, 44). - [ ] implement, test, commit per sub-item.

### Task 5: B9 — Deployability
Triage §3 B9 (item 45). - [ ] implement, test, commit per sub-item.

### Task 6: B5 — Hooks can be authored end to end
Triage §3 B5 (item 26). - [ ] implement, test, commit per sub-item.

### Task 7: B7 — Management and AI contracts
Triage §3 B7 (items 31, 40). - [ ] implement, test, commit per sub-item.

### Task 8: B6 — Authoring policy [security core]
Triage §3 B6 (items #270, #267 edit half). - [ ] implement, test, commit per sub-item.

### Task 9: B8 — Identity and caller hardening [security core]
Triage §3 B8 (items 39, 46(i), 36, 32). - [ ] implement, test, commit per sub-item.
