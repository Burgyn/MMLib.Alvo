# Snapshot Judge Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Catch, in the turn it happens, a Verify baseline (`*.verified.*`) that was rewritten without a source change that justifies it.

**Architecture:** Two-level Claude Code hook pattern. A PostToolUse recorder appends touched baseline paths to a per-turn ledger in the git dir; a Stop gate reads and clears that ledger once, drops no-op candidates via git, and blocks the turn with one instruction to dispatch a narrow read-only judge subagent. The ledger answers *did anything happen this run*, git answers *what changed*.

**Tech Stack:** bash (matching `scripts/`), `jq` for hook payload parsing, `git` for content, Claude Code hooks + subagent.

Spec: `docs/superpowers/specs/2026-07-25-snapshot-judge-gate-design.md`. Branch: `chore/snapshot-judge-gate` (already created; the spec is committed there as `02192bb`).

## Global Constraints

- **Hooks must never break a turn.** Use `set -uo pipefail` — never `set -e`. Every external call is guarded, every failure path is `exit 0`.
- **Fail open, but not silently.** A hook that cannot do its job exits 0. The judge, when it cannot judge (oversized diff), says so rather than guessing.
- **`jq` is a soft dependency.** `command -v jq >/dev/null 2>&1 || exit 0` at the top of every hook that parses a payload.
- **The ledger path is resolved via `git rev-parse --git-path edited-paths`**, never hardcoded to `.git/edited-paths` — this repo is worked on through worktrees, where `.git` is a file. The result may be relative and must be joined to the project root.
- **The ledger has exactly one owner.** Only `turn-review-gate` reads and deletes it. Claude Code runs an event's hooks in parallel, so a sibling Stop hook would race it. Future checks are functions inside `turn-review-gate`, never a second Stop hook.
- **The gate scopes by tool name in `matcher` and by path inside the recorder.** The `matcher` field filters tool names only; it cannot filter paths.
- **The judge is read-only:** `tools: Read, Grep, Bash`, no `Edit`/`Write`.
- **`suspicious` requires positive evidence from the closed fingerprint list; uncertainty resolves to `ok`.**
- Hook scripts live in `.claude/hooks/`, are executable (`chmod +x`), and start with `#!/usr/bin/env bash`.
- Commit messages follow Conventional Commits (enforced by the `commit-msg` Husky hook).

## File Structure

| File | Responsibility |
|---|---|
| `.claude/hooks/record-edited-paths` (create) | PostToolUse: append a touched `*.verified.*` path — or an `ACCEPT` marker for a shell-side baseline accept — to the per-turn ledger. No judgment, no blocking. |
| `.claude/hooks/turn-review-gate` (create) | Stop: sole ledger owner. Read once, delete once, run the check registry, emit at most one `block`. |
| `.claude/hooks/reset-edited-paths` (create) | SessionStart `startup\|clear`: delete a ledger orphaned by an ungraceful exit. |
| `.claude/agents/alvo-snapshot-judge.md` (create) | The judge's prompt: input gathering, the closed fingerprint list, the default-pass bias, the output bound. |
| `scripts/test-hooks` (create) | Plain-bash test harness driving each hook with crafted JSON payloads inside a throwaway git repo. |
| `.claude/settings.json` (modify) | Wire the three hooks alongside the existing `SessionStart` → `scripts/session-orient` entry. |
| `CLAUDE.md` (modify) | One short subsection so the gate is discoverable; it is not a Husky hook and does not belong in that section. |

---

### Task 1: Test harness + the recorder

**Files:**
- Create: `scripts/test-hooks`
- Create: `.claude/hooks/record-edited-paths`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `scripts/test-hooks` — harness with shell functions `setup_repo` (creates a throwaway git repo in `$TMP_ROOT`, prints its path), `assert_eq <expected> <actual> <label>`, `assert_empty <actual> <label>`, `assert_contains <needle> <haystack> <label>`, `ledger_path <repo>` (echoes the resolved ledger path), and a `PASS`/`FAIL` counter that makes the script exit non-zero if anything failed.
  - `.claude/hooks/record-edited-paths` — reads a PostToolUse JSON payload on stdin; appends one line to the ledger: either a repo-relative path matching `*.verified.*`, or the literal `ACCEPT`. Always exits 0.

- [ ] **Step 1: Write the failing test harness plus the recorder's tests**

Create `scripts/test-hooks`:

```bash
#!/usr/bin/env bash
# Tests for the Claude Code hooks in .claude/hooks/. Plain bash, no dependency:
# each hook is a process that reads a JSON payload on stdin, so the test drives
# it exactly the way Claude Code does — with a crafted payload and a throwaway
# git repo as CLAUDE_PROJECT_DIR.
#
# Run: scripts/test-hooks
set -uo pipefail

HOOKS_DIR="$(cd "$(dirname "$0")/../.claude/hooks" && pwd)"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

PASS=0
FAIL=0

pass() { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
fail() {
  FAIL=$((FAIL + 1))
  printf '  FAIL %s\n' "$1"
  printf '       expected: %s\n' "$2"
  printf '       actual:   %s\n' "$3"
}

assert_eq() { # <expected> <actual> <label>
  if [ "$1" = "$2" ]; then pass "$3"; else fail "$3" "$1" "$2"; fi
}

assert_empty() { # <actual> <label>
  if [ -z "$1" ]; then pass "$2"; else fail "$2" "(empty)" "$1"; fi
}

assert_contains() { # <needle> <haystack> <label>
  case "$2" in *"$1"*) pass "$3" ;; *) fail "$3" "contains '$1'" "$2" ;; esac
}

# A throwaway git repo with one committed baseline, so tests can distinguish
# "tracked and unchanged" from "tracked and modified" from "untracked".
setup_repo() { # -> prints the repo path
  local repo
  repo="$(mktemp -d "$TMP_ROOT/repo.XXXXXX")"
  git -C "$repo" init -q
  git -C "$repo" config user.email test@example.com
  git -C "$repo" config user.name test
  mkdir -p "$repo/test/Some.Tests" "$repo/src"
  printf 'committed\n' >"$repo/test/Some.Tests/Thing.verified.txt"
  printf 'x\n' >"$repo/src/Thing.cs"
  git -C "$repo" add -A
  git -C "$repo" commit -qm init
  printf '%s\n' "$repo"
}

ledger_path() { # <repo>
  local repo="$1" p
  p="$(git -C "$repo" rev-parse --git-path edited-paths 2>/dev/null)"
  case "$p" in /*) printf '%s\n' "$p" ;; *) printf '%s\n' "$repo/$p" ;; esac
}

# ==================================================================== recorder

echo "record-edited-paths"

repo="$(setup_repo)"
printf '{"tool_name":"Edit","tool_input":{"file_path":"%s/test/Some.Tests/Thing.verified.txt"}}' "$repo" \
  | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/record-edited-paths"
assert_eq "test/Some.Tests/Thing.verified.txt" "$(cat "$(ledger_path "$repo")" 2>/dev/null)" \
  "records a baseline path, repo-relative"

repo="$(setup_repo)"
printf '{"tool_name":"Edit","tool_input":{"file_path":"%s/src/Thing.cs"}}' "$repo" \
  | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/record-edited-paths"
assert_empty "$(cat "$(ledger_path "$repo")" 2>/dev/null)" \
  "ignores a non-baseline path"

repo="$(setup_repo)"
printf '{"tool_name":"Bash","tool_input":{"command":"dotnet verify accept"}}' \
  | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/record-edited-paths"
assert_eq "ACCEPT" "$(cat "$(ledger_path "$repo")" 2>/dev/null)" \
  "records ACCEPT for a shell-side baseline accept"

repo="$(setup_repo)"
printf '{"tool_name":"Bash","tool_input":{"command":"cp a.received.txt a.verified.txt"}}' \
  | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/record-edited-paths"
assert_eq "ACCEPT" "$(cat "$(ledger_path "$repo")" 2>/dev/null)" \
  "records ACCEPT for a received->verified copy"

repo="$(setup_repo)"
printf '{"tool_name":"Bash","tool_input":{"command":"dotnet test"}}' \
  | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/record-edited-paths"
assert_empty "$(cat "$(ledger_path "$repo")" 2>/dev/null)" \
  "ignores an unrelated Bash command"

repo="$(setup_repo)"
printf '{"tool_name":"Edit","tool_input":{}}' \
  | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/record-edited-paths"
assert_empty "$(cat "$(ledger_path "$repo")" 2>/dev/null)" \
  "survives a payload with no file_path"

# ======================================================================= total

printf '\n%d passed, %d failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
```

Then make it executable:

```bash
chmod +x scripts/test-hooks
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `scripts/test-hooks`

Expected: FAIL — every recorder assertion fails, because `.claude/hooks/record-edited-paths` does not exist yet, so bash reports `No such file or directory` and the ledger is never written. The script exits non-zero.

- [ ] **Step 3: Write the recorder**

Create `.claude/hooks/record-edited-paths`:

```bash
#!/usr/bin/env bash
# PostToolUse hook (GENERIC — no snapshot knowledge beyond its scope filter):
# records what this run touched into a per-turn ledger that Stop-hook consumers
# read and filter. Runs for the main agent and for subagents alike.
#
# WHY a ledger and not `git diff` in the Stop hook: git shows the working tree,
# which still holds an uncommitted baseline change from an earlier turn — a gate
# keyed on git alone fires on every turn of a pure conversation in which nothing
# happened. The ledger is the per-run truth.
#
# Never blocks. Never fails a turn: no `set -e`, every path exits 0.
set -uo pipefail

command -v jq >/dev/null 2>&1 || exit 0
payload="$(cat)" || exit 0
[ -n "$payload" ] || exit 0

root="${CLAUDE_PROJECT_DIR:-$(pwd)}"

# Worktree-aware: `.git` is a file in a worktree, so never hardcode .git/.
ledger="$(git -C "$root" rev-parse --git-path edited-paths 2>/dev/null)"
[ -n "$ledger" ] || exit 0
case "$ledger" in /*) ;; *) ledger="$root/$ledger" ;; esac

tool="$(printf '%s' "$payload" | jq -r '.tool_name // ""' 2>/dev/null)"

# Accepting a Verify baseline through the shell (`dotnet verify accept`, or a
# received->verified copy) produces NO Edit event, and it is the most natural way
# a baseline gets accepted. tool_input.command does not reveal WHICH baselines
# were accepted, so record a marker and let the gate resolve paths from git.
if [ "$tool" = "Bash" ]; then
  cmd="$(printf '%s' "$payload" | jq -r '.tool_input.command // ""' 2>/dev/null)"
  if printf '%s' "$cmd" | grep -Eqi 'verify[^|;&]*accept|(cp|mv)[^|;&]*received'; then
    printf 'ACCEPT\n' >>"$ledger" 2>/dev/null || true
  fi
  exit 0
fi

file="$(printf '%s' "$payload" | jq -r '.tool_input.file_path // ""' 2>/dev/null)"
[ -n "$file" ] || exit 0

# Repo-relative when under the project root (stable, readable); absolute otherwise.
rel="$file"
rootnorm="${root%/}"
case "$file" in "$rootnorm"/*) rel="${file#"$rootnorm"/}" ;; esac

# Coarse SCOPE filter. The settings.json PostToolUse `matcher` filters by TOOL
# NAME only, never by path, so path scoping lives here. Only Verify baselines
# matter while the snapshot check is the ledger's one consumer — loosen this when
# a future Stop check needs other paths.
case "$rel" in *.verified.*) ;; *) exit 0 ;; esac

printf '%s\n' "$rel" >>"$ledger" 2>/dev/null || true
exit 0
```

Then:

```bash
chmod +x .claude/hooks/record-edited-paths
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `scripts/test-hooks`

Expected: PASS — `6 passed, 0 failed`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add scripts/test-hooks .claude/hooks/record-edited-paths
git commit -m "feat(hooks): record touched Verify baselines into a per-turn ledger

The ledger is the per-run trigger a Stop gate needs: git alone would fire on
every turn of a pure conversation, because an uncommitted baseline change is
still in the working tree. Also records an ACCEPT marker for shell-side
accepts, which produce no Edit event at all."
```

---

### Task 2: The Stop gate

**Files:**
- Create: `.claude/hooks/turn-review-gate`
- Modify: `scripts/test-hooks` (append a `turn-review-gate` section before the `====== total` block)

**Interfaces:**
- Consumes: the ledger written by `.claude/hooks/record-edited-paths` (one repo-relative `*.verified.*` path or the literal `ACCEPT` per line); the harness functions from Task 1 (`setup_repo`, `ledger_path`, `assert_eq`, `assert_empty`, `assert_contains`).
- Produces: `.claude/hooks/turn-review-gate` — reads a Stop JSON payload on stdin, prints either nothing (exit 0) or exactly one JSON object `{"decision":"block","reason":"<text>"}` on stdout, and always exits 0. Internally exposes the check-registry convention: a shell function taking `<entries> <root>` and printing a block reason or nothing, registered in the `checks` list.

- [ ] **Step 1: Write the failing tests**

In `scripts/test-hooks`, insert this section immediately before the `# ======= total` block:

```bash
# ======================================================================== gate

echo "turn-review-gate"

run_gate() { # <repo> [stop_hook_active]
  local repo="$1" active="${2:-false}"
  printf '{"stop_hook_active":%s}' "$active" \
    | CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/turn-review-gate"
}

repo="$(setup_repo)"
assert_empty "$(run_gate "$repo")" "silent when there is no ledger"

repo="$(setup_repo)"
: >"$(ledger_path "$repo")"
assert_empty "$(run_gate "$repo")" "silent when the ledger is empty"

# A touched-but-unchanged baseline is a no-op: the agent rewrote identical bytes,
# or edited and reverted inside the run. Nothing to judge.
repo="$(setup_repo)"
printf 'test/Some.Tests/Thing.verified.txt\n' >"$(ledger_path "$repo")"
assert_empty "$(run_gate "$repo")" "drops a tracked no-op candidate"

# A really-changed baseline must block.
repo="$(setup_repo)"
printf 'changed\n' >"$repo/test/Some.Tests/Thing.verified.txt"
printf 'test/Some.Tests/Thing.verified.txt\n' >"$(ledger_path "$repo")"
out="$(run_gate "$repo")"
assert_contains '"decision":"block"' "$(printf '%s' "$out" | jq -c .)" \
  "blocks on a changed baseline"
assert_contains 'alvo-snapshot-judge' "$out" \
  "block reason names the judge agent"
assert_contains 'Thing.verified.txt' "$out" \
  "block reason names the changed baseline"

# A brand-new baseline does not appear in `git diff`, so it must be kept.
repo="$(setup_repo)"
printf 'new\n' >"$repo/test/Some.Tests/Fresh.verified.txt"
printf 'test/Some.Tests/Fresh.verified.txt\n' >"$(ledger_path "$repo")"
assert_contains 'Fresh.verified.txt' "$(run_gate "$repo")" \
  "keeps an untracked (new) baseline"

# An edit followed by a delete inside the run leaves nothing on disk.
repo="$(setup_repo)"
printf 'test/Some.Tests/Gone.verified.txt\n' >"$(ledger_path "$repo")"
assert_empty "$(run_gate "$repo")" "drops a candidate that no longer exists"

# The ACCEPT marker carries no path; the gate resolves paths from git.
repo="$(setup_repo)"
printf 'changed\n' >"$repo/test/Some.Tests/Thing.verified.txt"
printf 'ACCEPT\n' >"$(ledger_path "$repo")"
assert_contains 'Thing.verified.txt' "$(run_gate "$repo")" \
  "ACCEPT marker resolves paths from git"

# Two baselines, one block — not one block per file.
repo="$(setup_repo)"
printf 'changed\n' >"$repo/test/Some.Tests/Thing.verified.txt"
printf 'new\n' >"$repo/test/Some.Tests/Fresh.verified.txt"
printf 'test/Some.Tests/Thing.verified.txt\ntest/Some.Tests/Fresh.verified.txt\n' \
  >"$(ledger_path "$repo")"
assert_eq "1" "$(run_gate "$repo" | jq -s 'length')" \
  "emits exactly one block for two baselines"

# The ledger has ONE owner: it is gone after a single read, so a block-induced
# re-Stop finds nothing and cannot loop.
repo="$(setup_repo)"
printf 'changed\n' >"$repo/test/Some.Tests/Thing.verified.txt"
printf 'test/Some.Tests/Thing.verified.txt\n' >"$(ledger_path "$repo")"
run_gate "$repo" >/dev/null
if [ -f "$(ledger_path "$repo")" ]; then
  fail "clears the ledger after one read" "(no ledger file)" "ledger still present"
else
  pass "clears the ledger after one read"
fi
assert_empty "$(run_gate "$repo")" "second run is silent"

# stop_hook_active is the belt-and-braces guard against a block-induced re-Stop.
repo="$(setup_repo)"
printf 'changed\n' >"$repo/test/Some.Tests/Thing.verified.txt"
printf 'test/Some.Tests/Thing.verified.txt\n' >"$(ledger_path "$repo")"
assert_empty "$(run_gate "$repo" true)" "silent when stop_hook_active is true"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `scripts/test-hooks`

Expected: the 6 recorder assertions still pass; every gate assertion fails, because `.claude/hooks/turn-review-gate` does not exist. Exit code non-zero.

- [ ] **Step 3: Write the gate**

Create `.claude/hooks/turn-review-gate`:

```bash
#!/usr/bin/env bash
# Stop hook: GENERIC post-turn review dispatcher.
#
# Reads the per-turn ledger (written by record-edited-paths) ONCE, clears it
# ONCE, then runs a set of independent CHECKS over what this run touched. Each
# check prints a block reason (or nothing). Reasons are aggregated into a single
# Stop 'block' so the agent addresses them before finishing the turn.
#
# WHY one hook, not one-per-check: Claude Code runs an event's hooks in PARALLEL,
# as separate processes. Cooperating logic (read the ledger, clear it, decide)
# MUST live in ONE process — a sibling hook would race it. Add a future check as
# a function below and register it in `checks` — NEVER as a second Stop hook.
#
# Holds no deterministic rule content (arch tests and the public-API approval
# test own those). A check only decides "does what this run touched warrant a
# judgment review, and what should the agent be told?".
set -uo pipefail

command -v jq >/dev/null 2>&1 || exit 0
payload="$(cat)" || payload=''

# Belt-and-braces against a block-induced re-Stop; clearing the ledger below is
# the real guard.
if [ -n "$payload" ]; then
  active="$(printf '%s' "$payload" | jq -r '.stop_hook_active // false' 2>/dev/null)"
  [ "$active" = "true" ] && exit 0
fi

root="${CLAUDE_PROJECT_DIR:-$(pwd)}"

ledger="$(git -C "$root" rev-parse --git-path edited-paths 2>/dev/null)"
[ -n "$ledger" ] || exit 0
case "$ledger" in /*) ;; *) ledger="$root/$ledger" ;; esac
[ -f "$ledger" ] || exit 0

# Read this run's ledger, then clear it immediately. Single owner => no race with
# a sibling hook, and nothing left to re-read on a re-Stop => no loop.
entries="$(sort -u "$ledger" 2>/dev/null)"
rm -f "$ledger" 2>/dev/null || true
[ -n "$entries" ] || exit 0

# =============================================================================
# CHECKS — each takes <entries> <root> and prints a block reason, or nothing.
# Add a new check = write a function here + add it to `checks` below.
# =============================================================================

# --- Verify baselines: a *.verified.* file moved this run ---------------------
check_verified_baselines() {
  local entries="$1" root="$2"
  local candidates listed f surviving=''

  # Paths the recorder captured directly.
  candidates="$(printf '%s\n' "$entries" | grep -E '\.verified\.' || true)"

  # An ACCEPT marker means a baseline was accepted through the shell, which does
  # not reveal paths — resolve them from git instead. `sed` strips the two status
  # columns, then the rename arrow if present (`R old -> new` keeps `new`).
  if printf '%s\n' "$entries" | grep -qx 'ACCEPT'; then
    listed="$(git -C "$root" status --porcelain --untracked-files=all 2>/dev/null \
      | sed -e 's/^...//' -e 's/.* -> //' | grep -E '\.verified\.' || true)"
    candidates="$(printf '%s\n%s\n' "$candidates" "$listed")"
  fi

  candidates="$(printf '%s\n' "$candidates" | grep -v '^[[:space:]]*$' | sort -u || true)"
  [ -n "$candidates" ] || return 0

  # Drop no-ops. A TRACKED file whose content matches HEAD was rewritten
  # byte-identically, or edited and reverted, inside this run — nothing to judge.
  # An UNTRACKED file is a new baseline and never appears in a diff, so keep it
  # as long as it is still on disk.
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    if git -C "$root" ls-files --error-unmatch -- "$f" >/dev/null 2>&1; then
      git -C "$root" diff --quiet HEAD -- "$f" 2>/dev/null && continue
    else
      [ -e "$root/$f" ] || continue
    fi
    surviving="$surviving$f, "
  done <<EOF
$candidates
EOF

  [ -n "$surviving" ] || return 0

  printf 'Verify baselines changed this run: %s. ' "${surviving%, }"
  printf 'Before finishing, invoke the alvo-snapshot-judge agent on these files and address its findings. '
  printf 'It judges only whether the new baseline is justified by the accompanying source change — '
  printf 'do NOT re-check mechanical rules (the arch tests and the public-API approval test own those).'
}

# Registry of active checks — they run in THIS process, sequentially.
checks="check_verified_baselines"

# =============================================================================

reasons=''
for check in $checks; do
  r="$("$check" "$entries" "$root" 2>/dev/null)" || r=''
  [ -n "$r" ] || continue
  if [ -n "$reasons" ]; then
    reasons="$reasons

$r"
  else
    reasons="$r"
  fi
done

[ -n "$reasons" ] || exit 0

jq -n --arg reason "$reasons" '{decision:"block", reason:$reason}'
exit 0
```

Then:

```bash
chmod +x .claude/hooks/turn-review-gate
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `scripts/test-hooks`

Expected: PASS — `19 passed, 0 failed`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add .claude/hooks/turn-review-gate scripts/test-hooks
git commit -m "feat(hooks): gate a turn on changed Verify baselines

Sole owner of the per-turn ledger: reads once, clears once, runs a registry of
judgment checks, and emits at most one Stop block. A future check is a function
here, never a second Stop hook — an event's hooks run in parallel and would race
the ledger.

The check drops touched-but-unchanged baselines via git, keeps untracked ones
(a new baseline is absent from a diff), and resolves paths from git status when
the ledger only carries an ACCEPT marker."
```

---

### Task 3: The reset hook and the settings wiring

**Files:**
- Create: `.claude/hooks/reset-edited-paths`
- Modify: `scripts/test-hooks` (append a `reset-edited-paths` section before the `====== total` block)
- Modify: `.claude/settings.json`

**Interfaces:**
- Consumes: `ledger_path`, `setup_repo`, `assert_eq`, `pass`, `fail` from Task 1; the ledger contract from Tasks 1–2.
- Produces: `.claude/hooks/reset-edited-paths` — deletes the ledger, always exits 0, reads no payload. And a `.claude/settings.json` in which all three hooks are wired.

- [ ] **Step 1: Write the failing tests**

In `scripts/test-hooks`, insert this section immediately before the `# ======= total` block:

```bash
# ======================================================================= reset

echo "reset-edited-paths"

repo="$(setup_repo)"
printf 'test/Some.Tests/Thing.verified.txt\n' >"$(ledger_path "$repo")"
CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/reset-edited-paths" </dev/null
if [ -f "$(ledger_path "$repo")" ]; then
  fail "deletes an orphaned ledger" "(no ledger file)" "ledger still present"
else
  pass "deletes an orphaned ledger"
fi

repo="$(setup_repo)"
CLAUDE_PROJECT_DIR="$repo" bash "$HOOKS_DIR/reset-edited-paths" </dev/null
assert_eq "0" "$?" "exits 0 when there is no ledger"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `scripts/test-hooks`

Expected: the 19 earlier assertions pass; `deletes an orphaned ledger` fails because the hook does not exist. Exit code non-zero.

- [ ] **Step 3: Write the reset hook**

Create `.claude/hooks/reset-edited-paths`:

```bash
#!/usr/bin/env bash
# SessionStart hook (safety net): delete the per-turn ledger at a FRESH session
# start, so a ledger orphaned by an ungraceful exit — process killed, or a crash
# before the Stop gate ran — cannot cause a spurious review on the next
# session's first turn.
#
# Wire ONLY on the fresh-start sources `startup|clear`, NEVER on
# `compact`/`resume`, where a turn's already-recorded but not-yet-processed
# edits would be wrongly dropped. Never blocks.
set -uo pipefail

root="${CLAUDE_PROJECT_DIR:-$(pwd)}"

ledger="$(git -C "$root" rev-parse --git-path edited-paths 2>/dev/null)"
[ -n "$ledger" ] || exit 0
case "$ledger" in /*) ;; *) ledger="$root/$ledger" ;; esac

rm -f "$ledger" 2>/dev/null || true
exit 0
```

Then:

```bash
chmod +x .claude/hooks/reset-edited-paths
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `scripts/test-hooks`

Expected: PASS — `21 passed, 0 failed`, exit code 0.

- [ ] **Step 5: Wire all three hooks into settings**

Replace the whole content of `.claude/settings.json` with:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit|Bash",
        "hooks": [
          {
            "type": "command",
            "command": "bash \"${CLAUDE_PROJECT_DIR}/.claude/hooks/record-edited-paths\"",
            "timeout": 10
          }
        ]
      }
    ],
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "bash scripts/session-orient"
          }
        ]
      },
      {
        "matcher": "startup|clear",
        "hooks": [
          {
            "type": "command",
            "command": "bash \"${CLAUDE_PROJECT_DIR}/.claude/hooks/reset-edited-paths\"",
            "timeout": 10
          }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "*",
        "hooks": [
          {
            "type": "command",
            "command": "bash \"${CLAUDE_PROJECT_DIR}/.claude/hooks/turn-review-gate\"",
            "timeout": 60
          }
        ]
      }
    ]
  }
}
```

Note the existing `SessionStart` → `scripts/session-orient` entry is preserved untouched, as its own matcher-less block; the reset hook is a second block scoped to `startup|clear`.

- [ ] **Step 6: Verify the settings file is valid JSON and the wiring is right**

Run: `jq -e '.hooks | (.PostToolUse | length) == 1 and (.SessionStart | length) == 2 and (.Stop | length) == 1' .claude/settings.json`

Expected: prints `true`, exit code 0.

- [ ] **Step 7: Commit**

```bash
git add .claude/hooks/reset-edited-paths scripts/test-hooks .claude/settings.json
git commit -m "feat(hooks): reset an orphaned ledger and wire the gate

The reset runs only on startup|clear — on compact/resume it would drop a turn's
already-recorded but not-yet-processed edits. The Stop gate gets a 60s timeout
because its block leads to a subagent dispatch, not because the hook itself is
slow."
```

---

### Task 4: The judge agent and the docs entry

**Files:**
- Create: `.claude/agents/alvo-snapshot-judge.md`
- Modify: `CLAUDE.md` (new subsection after the existing `## Git hooks` section)

**Interfaces:**
- Consumes: the block reason emitted by `check_verified_baselines` in Task 2, which names the changed baselines and instructs the agent to invoke `alvo-snapshot-judge`.
- Produces: the `alvo-snapshot-judge` subagent, dispatchable by name, returning per-baseline `ok`/`suspicious` verdicts.

- [ ] **Step 1: Write the judge agent**

Create `.claude/agents/alvo-snapshot-judge.md`:

```markdown
---
name: alvo-snapshot-judge
description: Narrow, fast judge for a changed Verify baseline (*.verified.*) — decides only whether the new baseline is justified by the accompanying source change and the active plan. Invoked when a baseline moved during a turn. Read-only; returns a per-file verdict, never edits anything.
tools: Read, Grep, Bash
model: haiku
---

# Alvo snapshot judge

A Verify baseline is the one place in this repo where a failing test can be
made green with **no change to product code**: copy `received` over `verified`,
or run `dotnet verify accept`, and the suite stops describing intended
behaviour and starts encoding whatever the code currently does.

You judge exactly one question per baseline: **is this new baseline justified?**
You are deliberately narrow so you are fast. You are read-only — you raise
concerns, you never fix anything.

## Gather your own inputs

You are invoked fresh, with no memory of the conversation that changed the
baselines. Use `Bash` for read-only inspection only (`git diff`, `git status`,
`git log`) — never write, stage, commit, or push.

1. `git status --porcelain --untracked-files=all` — the authoritative list of
   what changed. Judge only the `*.verified.*` files in it (the invoking message
   names them; this is your cross-check).
2. Per baseline: `git diff HEAD -- <file>`. If the file is **untracked** it will
   not appear in a diff — `Read` it whole instead.
3. The accompanying source change: `git diff HEAD --stat -- src/` first, then
   `git diff HEAD -- src/` when it is small enough to read.
4. Intent evidence: the newest file in `docs/superpowers/plans/` (and the spec it
   points at, if any). This is what tells you whether a behaviour change was
   *planned* rather than laundered.

## The verdict is asymmetric

Return `suspicious` **only** when you find one of these fingerprints. The list is
closed — do not invent new grounds:

- **No source change at all.** The baseline moved but nothing in `src/` changed
  anywhere in the working tree. This is the pure laundering fingerprint.
- **A `PublicApi.*` baseline lost surface.** A member or type disappeared, or a
  signature narrowed, and neither the plan nor the spec mentions the break.
- **The baseline contradicts its own test's name.** For example
  `Add_column_sql_is_stable` now containing a `DROP`. Judge the content against
  what the test name claims to assert.
- **Weakened semantics.** Required became optional; a validation error
  disappeared; a `negative-error-output` baseline now expects fewer errors.
- **The baseline change is broader than the source change can explain.** A
  one-line source edit against a wholly reshaped model.

Everything else is `ok`. **Uncertainty resolves to `ok`** — say `ok` and move on.
A gate that cries wolf gets switched off, and there are real backstops under you
(arch tests, the public-API approval test) and over you (`alvo-plan-guard`,
`/code-review`, `/security-review`, CodeRabbit and CodeQL on the PR). You only
have to catch the obvious.

Two cases that are explicitly **normal** — never flag them:

- **A new baseline for a new test.** This is the most common legitimate change.
- **A destructive operation in a SQL baseline whose test is about that
  operation.** `Drop_column_sql_is_stable.verified.txt` contains `DROP COLUMN`
  because that is the point of the test. Only a *mismatch* with the test name
  counts.

## Do not judge

Code quality, test design, naming, coverage, or anything the arch tests and the
public-API approval test already enforce mechanically. Reporting those is noise.

## When you cannot judge

If a baseline's diff exceeds roughly 400 lines, do not guess. Report
`not judged — <file> diff is ~<N> lines, review manually` for that file and
carry on with the others.

## Output

One line per baseline, then one overall line. At most two sentences of reasoning
per baseline — the bound is what keeps this a judgment and not a review essay.

    <file>: ok | suspicious | not judged — <at most two sentences>
    ...
    Overall: ok | suspicious (<n> of <m> baselines)
```

- [ ] **Step 2: Verify the agent file is well-formed**

Claude Code discovers agents from the YAML frontmatter of `.claude/agents/*.md` —
there is no registry file to update, and a **new session** is needed before the
name becomes dispatchable.

Run: `head -6 .claude/agents/alvo-snapshot-judge.md`

Expected: the first line is `---`, followed by `name: alvo-snapshot-judge`, a
`description:` line, `tools: Read, Grep, Bash`, `model: haiku`, and a closing
`---`. Confirm `Edit` and `Write` are absent from `tools` — the judge must not be
able to fix what it flags.

- [ ] **Step 3: Document the gate in CLAUDE.md**

In `CLAUDE.md`, immediately after the existing `## Git hooks` section, add:

```markdown
## Claude Code hooks

Distinct from the Husky git hooks above: these run inside an agent's turn, not
around a commit. `.claude/hooks/record-edited-paths` (PostToolUse) records every
touched `*.verified.*` baseline into a per-turn ledger in the git dir;
`.claude/hooks/turn-review-gate` (Stop) reads and clears that ledger once and,
if a baseline really moved, blocks the turn with an instruction to dispatch the
read-only `alvo-snapshot-judge` — a Verify baseline is the one place a test can
be made green with no product-code change. `reset-edited-paths` clears a ledger
orphaned by an ungraceful exit. The gate is a **registry**: add a future
judgment check as a function inside `turn-review-gate`, never as a second Stop
hook (an event's hooks run in parallel and would race the ledger). Tests:
`scripts/test-hooks`. Design: `docs/superpowers/specs/2026-07-25-snapshot-judge-gate-design.md`.
```

- [ ] **Step 4: Run the full hook test suite once more**

Run: `scripts/test-hooks`

Expected: PASS — `21 passed, 0 failed`, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add .claude/agents/alvo-snapshot-judge.md CLAUDE.md
git commit -m "feat(hooks): add the alvo-snapshot-judge agent

Narrow read-only judge with a closed fingerprint list and a default-pass bias:
uncertainty resolves to ok, because a blocking gate that cries wolf gets
switched off and real backstops sit both under it and over it. The
destructive-SQL fingerprint is relative to the test's own name — 16 existing
baselines legitimately contain DROP."
```

---

### Task 5: Manual acceptance

**Files:** none — this task changes nothing. It exercises the wired gate end to end, which the harness cannot do (the harness drives hooks as processes; it cannot drive Claude Code).

**Interfaces:**
- Consumes: everything from Tasks 1–4, wired and committed.

This task requires a **fresh session** (`/clear` or restart), because `.claude/settings.json` hooks and `.claude/agents/*` are read at session start.

- [ ] **Step 1: Confirm the happy path is silent**

Start a fresh session. Ask the agent to make a trivial, unrelated edit (e.g. add a comment line to `scripts/test-ring0`) and finish the turn.

Expected: the turn ends normally. No block, no judge dispatch, no output from the gate. Confirm the ledger is gone: `test -f "$(git rev-parse --git-path edited-paths)" && echo LEAKED || echo clean` → `clean`.

- [ ] **Step 2: Confirm the gate fires on laundering (must fire)**

In a fresh session, hand-simulate the failure mode the gate exists for: a baseline changes while `src/` stays untouched.

The edit **must go through the agent's own `Edit` tool** — the recorder is a PostToolUse hook, so an edit you make in your own shell produces no event and the gate will correctly stay silent. So ask the agent, in that session:

> Append a line `# laundered` to the end of `test/MMLib.Alvo.Schema.Tests/canonical-complex-crm.verified.txt` and change nothing else.

Then let the turn finish.

Expected: the Stop gate blocks with `Verify baselines changed this run: test/MMLib.Alvo.Schema.Tests/canonical-complex-crm.verified.txt.` and the instruction to invoke `alvo-snapshot-judge`. The judge returns **`suspicious`**, citing the no-source-change fingerprint.

Then revert: `git checkout -- test/MMLib.Alvo.Schema.Tests/canonical-complex-crm.verified.txt`

- [ ] **Step 3: Confirm the judge stays quiet on a legitimate change (must not cry wolf)**

In a fresh session, make a real source change with a matching baseline update — the smallest honest one available is to add a genuinely new snapshot test with a new baseline (a new test plus its new `*.verified.txt`).

Expected: the gate blocks (a baseline moved, so it must), the judge is dispatched, and it returns **`ok`** — a new baseline for a new test is explicitly normal.

Then revert the scratch test and baseline.

- [ ] **Step 4: Record the outcome**

Append the three observed outcomes to the spec under a new `## Acceptance` heading — actual verdicts, not "as designed" — and commit:

```bash
git add docs/superpowers/specs/2026-07-25-snapshot-judge-gate-design.md
git commit -m "docs(hooks): record snapshot gate acceptance results"
```

- [ ] **Step 5: Pre-PR checks**

Run `scripts/test-ring2`, then dispatch the `alvo-plan-guard` subagent as the last check per `CLAUDE.md`. Note: this change touches no `src/` code, so `/security-review` is not indicated; run `/code-review medium` over the shell scripts.

---

## Notes for the implementer

**The single thing to not get wrong:** the ledger has exactly one owner. If you
are tempted to add a second Stop hook — for a new check, for logging, for
anything — don't. Claude Code runs an event's hooks in parallel as separate
processes; a sibling would race `turn-review-gate` for the read-and-clear. Add a
function and register it in `checks`.

**Why `set -uo pipefail` and not `set -euo pipefail`:** with `-e`, a
non-matching `grep` (exit 1) or an absent file kills the hook mid-way. A hook
that dies mid-way can leave the ledger both unread and unclear, which is worse
than doing nothing. Every failure path here exits 0 deliberately.

**Known limitation, do not try to fix it in this plan:** paths with spaces or
non-ASCII characters come back quoted from `git status --porcelain`, so the
`ACCEPT` resolution path would mishandle them. No baseline in this repo has such
a name. If one ever does, switch that call to `git status --porcelain -z` and
read NUL-delimited.
