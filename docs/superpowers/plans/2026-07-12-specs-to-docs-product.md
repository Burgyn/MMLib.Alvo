# Move specs/ into docs/product/ Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the three files under `specs/` into a new `docs/product/` directory, update the handful of files that reference the old `specs/` path, and leave historical brainstorming/plan records untouched.

**Architecture:** Pure file relocation + text edits. No code, no tests to run beyond `ls`/`grep` verification.

**Tech Stack:** git, plain Markdown/JSON files.

## Global Constraints

- Work happens on branch `docs-specs-to-product` (already created; do not commit to `main` directly — every change lands via a reviewed PR, per `CLAUDE.md`).
- File names inside `specs/` do not change, only their containing directory (spec: `docs/superpowers/specs/2026-07-12-specs-to-docs-product-design.md`).
- Do **not** edit `docs/superpowers/plans/2026-07-11-repo-bootstrap.md`, `docs/superpowers/specs/2026-07-11-repo-bootstrap-design.md`, or `docs/superpowers/specs/2026-07-12-contributing-cla-design.md` — they are dated historical records and are explicitly out of scope per the design doc.
- Only touch `CLAUDE.md`, `CONTRIBUTING.md`, and `docs/architecture/package-boundary.md` for reference updates.

---

### Task 1: Move the spec files into `docs/product/`

**Files:**
- Create dir: `docs/product/`
- Move: `specs/alvo-specifikacia.md` → `docs/product/alvo-specifikacia.md`
- Move: `specs/baas-analyza.md` → `docs/product/baas-analyza.md`
- Move: `specs/alvo-descriptor.schema.json` → `docs/product/alvo-descriptor.schema.json`
- Remove: `specs/` (once empty)

**Interfaces:**
- Produces: the path `docs/product/alvo-specifikacia.md`, `docs/product/baas-analyza.md`, `docs/product/alvo-descriptor.schema.json` that Task 2's edits point at.

- [ ] **Step 1: Create the target directory and move the three files with `git mv`**

```bash
mkdir -p docs/product
git mv specs/alvo-specifikacia.md docs/product/alvo-specifikacia.md
git mv specs/baas-analyza.md docs/product/baas-analyza.md
git mv specs/alvo-descriptor.schema.json docs/product/alvo-descriptor.schema.json
```

- [ ] **Step 2: Remove the now-empty `specs/` directory**

```bash
rmdir specs
```

Expected: command succeeds silently (fails if any file was left behind — check with `ls specs/` first if it errors).

- [ ] **Step 3: Verify the new layout**

Run: `ls docs/product/ && ls specs 2>&1`
Expected:
```
alvo-descriptor.schema.json
alvo-specifikacia.md
baas-analyza.md
ls: specs: No such file or directory
```

- [ ] **Step 4: Commit**

```bash
git add -A specs docs/product
git commit -m "docs: move specs/ into docs/product/"
```

---

### Task 2: Update references to the old `specs/` path

**Files:**
- Modify: `CLAUDE.md:3` (root, not the plans/specs copies)
- Modify: `CONTRIBUTING.md:8`
- Modify: `docs/architecture/package-boundary.md:4`

**Interfaces:**
- Consumes: the new path `docs/product/alvo-specifikacia.md` produced by Task 1.

- [ ] **Step 1: Update `CLAUDE.md`**

Current text (lines 3-5):

```markdown
Alvo is a .NET-native Backend-as-a-Service. The product specs live in `specs/`
(`alvo-specifikacia.md` = delivery strategy & technical spec; `baas-analyza.md` =
domain analysis). Architecture notes live in `docs/`.
```

New text:

```markdown
Alvo is a .NET-native Backend-as-a-Service. The product specs live in `docs/product/`
(`alvo-specifikacia.md` = delivery strategy & technical spec; `baas-analyza.md` =
domain analysis). Architecture notes live in `docs/`.
```

- [ ] **Step 2: Update `CONTRIBUTING.md`**

Current text (lines 7-8):

```markdown
If you want the full picture of what Alvo is and where it's going, the product specs
live in [`specs/`](specs/) and architecture notes in [`docs/`](docs/).
```

New text:

```markdown
If you want the full picture of what Alvo is and where it's going, the product specs
live in [`docs/product/`](docs/product/) and architecture notes in [`docs/`](docs/).
```

- [ ] **Step 3: Update `docs/architecture/package-boundary.md`**

Current text (lines 3-4):

```markdown
> The rule that decides what becomes a separate NuGet package in the
> `MMLib.Alvo.*` family. Source: spec `specs/alvo-specifikacia.md` §1.1.
```

New text:

```markdown
> The rule that decides what becomes a separate NuGet package in the
> `MMLib.Alvo.*` family. Source: spec `docs/product/alvo-specifikacia.md` §1.1.
```

- [ ] **Step 4: Verify no remaining references to the old path outside the excluded historical files**

Run:

```bash
grep -rn '`specs/' --include='*.md' --include='*.json' . | grep -v '^./docs/superpowers/'
```

Expected: no output (empty).

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md CONTRIBUTING.md docs/architecture/package-boundary.md
git commit -m "docs: point specs/ references at docs/product/"
```

---

## Self-Review Notes

- **Spec coverage:** design doc's three moved files → Task 1; three referencing files → Task 2; historical docs explicitly excluded via Global Constraints and Task 2 Step 4's grep exclusion. No gaps.
- **Placeholder scan:** none — every step has exact commands/text.
- **Type consistency:** n/a (no code).
