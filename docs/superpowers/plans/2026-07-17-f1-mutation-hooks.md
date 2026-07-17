# F1 Mutation testing + git hooks & PR process — Implementation Plan

> **For agentic workers:** superpowers:executing-plans. #13 is config wired dormant (no core to mutate); #15 hooks are shell — each task ends in a concrete verification. Added to PR #59; completes F1.

## Global Constraints

Same as the rest of F1 (CPM, no inline comments / English-only, file-scoped namespaces, warnings-as-errors, never push to `main`). Shell scripts stay LF.

---

### Task 1: Stryker mutation gate (dormant)

**Files:** `.config/dotnet-tools.json` (add `dotnet-stryker`), `stryker-config.json` (create), `.github/workflows/mutation.yml` (create).

- [ ] `dotnet tool install dotnet-stryker`; confirm `dotnet stryker --version` runs on net10.
- [ ] `stryker-config.json`: `mutate` globs on `src/MMLib.Alvo/**` (future core), `thresholds.break = 80`. Valid JSON.
- [ ] `mutation.yml`: `on: pull_request: paths: [src/MMLib.Alvo/**, stryker-config.json]`; a guard step that skips if the core project is absent; else `dotnet tool restore` + `dotnet stryker`. YAML lints.
- [ ] **Verify:** tool installs; `python3 -c json.load` on the config; `yaml.safe_load` on the workflow.
- [ ] Commit.

### Task 2: pre-commit — format + fast tests on code commits

**Files:** `.githooks/pre-commit` (extend).

- [ ] Keep the brief-freshness block. Add: if staged files match `\.(cs|csproj|props|targets)$` → `dotnet format --verify-no-changes` then `scripts/test-ring0`.
- [ ] **Verify:** stage a deliberately mis-formatted `.cs` → hook fails; a docs-only staged change → hook skips the code gate.
- [ ] Commit.

### Task 3: commit-msg — Conventional Commits

**Files:** `.githooks/commit-msg` (create).

- [ ] Validate subject against `^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\(...\))?!?: .+`; exempt Merge/Revert/fixup!/squash!.
- [ ] **Verify:** a bad subject is rejected, a good one and a merge subject pass (run the hook directly on sample files).
- [ ] Commit.

### Task 4: PR template

**Files:** `.github/pull_request_template.md` (create).

- [ ] Issue link, DoD checklist, security-core prompt (`needs-deep-review` + `alvo-security-core-review`), `alvo-plan-guard` reminder.
- [ ] Commit.

### Task 5: Verify, guard, push

- [ ] `dotnet build -c Release`, `dotnet format --verify-no-changes`, `bash scripts/test-ring2` green.
- [ ] Update memory; move `docs/PLAN.md` `← YOU ARE HERE` proposal is **not** applied by me (plan-guard proposes; a human applies) — but F1 will be complete on merge, so note it.
- [ ] `alvo-plan-guard` over the full #11+#12+#13+#14+#15 diff.
- [ ] Push (updates #59); update PR title/body to cover all five issues. CI green incl. Windows.

## Self-review notes

- #13 dormant (paths don't exist) — no false green: a guard skips if core absent, and the actual gate activates when the core lands. ✓
- Hooks are LF; require the documented `core.hooksPath` one-time setup. ✓
