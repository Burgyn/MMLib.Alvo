---
name: alvo-pr-report
description: Use when opening a PR in MMLib.Alvo, or when asked for an overview of an existing PR — builds the fixed 8-section visual report (published Artifact) that lets the maintainer judge the change without reading the diff, and writes the short PR body that links to it.
---

# Alvo PR report

The maintainer does not read diffs. He reads **one page per PR**, always the
same eight sections in the same order, and from it he has to be able to answer
four questions: *what can Alvo now do that it could not*, *how was it done*,
*is this a good direction*, and *what comes next*. This skill produces that page.

Run it **after `alvo-plan-guard`** (its verdict is an input) and **before
`gh pr create`**. It also runs on demand over any existing PR:
`generate the PR report for #163`.

## Pipeline

1. **Pick the output path** — `<scratchpad>/pr-report-<number-or-branch>.html`.
   Same path on every regeneration of the same PR: the Artifact then redeploys
   to the same URL instead of claiming a new one.
2. **Dispatch `alvo-pr-reporter`** (Agent tool) with: the output path, the PR
   number or branch, the plan-guard verdict, and any review output from this
   session. It re-derives everything else from the repo. Dispatching it as a
   subagent is the point — it starts with no memory of writing the code, so it
   has nothing to defend, and its input-gathering never enters your context.
3. **Read the returned file** before publishing it. You are publishing content;
   you check it first. Verify the self-check list actually holds — 8 sections,
   no `{{` slots, section 7 non-empty, gates not optimistically green.
4. **Publish** with the Artifact tool: the returned `TITLE`, a one-sentence
   `description`, favicon `📐` on first publish (omit it on every redeploy).
   Load the `artifact-design` skill first, as its own rules require.
5. **Write the PR body** — the reporter's `PR-BODY` block, English, with the
   artifact URL on a `Full report:` line. Five to eight lines. The page carries
   the detail; the body is a pointer, not a second copy of it.

## The contract that makes it useful

`template.html` in this directory is **fixed**. Its value is that the maintainer
never re-orients: section 4 is always the public API delta, section 7 is always
what should worry him. Do not add a section because this PR feels special, and
do not drop one because it is empty — an empty section states its empty case in
one sentence.

The caps in the reporter agent are the other half of the contract. A report over
~900 words stops being read, and the whole point is that it gets read instead of
the diff. When there is more to say, the spec in `docs/superpowers/specs/` is
where it goes, and the page links to it.

Three rules carry most of the quality:

- **Evidence over assertion.** Every claim in sections 1 and 6 traces to a named
  test, a measurement in `docs/superpowers/specs/evidence/`, or a gate's output.
  What cannot be traced is marked `unverified`, never quietly dropped.
- **Section 4 comes from `PublicApi.*.verified.txt`**, the approval baselines —
  the diff of those files is the public surface change, by definition. No other
  source is admissible, and no baseline diff means nothing public moved.
- **Section 7 is adversarial and never empty.** If nothing is risky, it names the
  assumption whose failure would cost most.

## When it is not worth it

A docs-only PR, a dependabot bump, a one-line CI fix: skip the page, write a
normal short PR body. The report earns its cost when the PR changes what Alvo
can do.
