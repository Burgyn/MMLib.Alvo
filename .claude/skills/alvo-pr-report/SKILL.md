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

1. **Read the language off the arguments** — a bare argument is a language code
   when it is `en`, or when `references/labels.<it>.md` exists. **`en` is the
   default** when none is given, and anything that is neither is the PR number or
   branch — so `/pr-report 174 sk`, `/pr-report sk` and `/pr-report 174` are all
   unambiguous, and a new language becomes selectable by its labels file landing,
   with no parser to update. A code with no labels file is *not* silently treated
   as a branch name: say the language is not available and ask, because a typo'd
   `sl` would otherwise become a branch nobody has. See *Language* below for what
   a non-English report does and does not translate.
2. **Pick the output path** — `<scratchpad>/pr-report-<number-or-branch>.html`.
   Same path on every regeneration of the same PR: the Artifact then redeploys
   to the same URL instead of claiming a new one. **The language is not part of
   the path** — a report regenerated in another language replaces the page rather
   than forking it, because two half-maintained translations of one PR is the
   outcome nobody wants.
3. **Dispatch `alvo-pr-reporter`** (Agent tool) with: the output path, the PR
   number or branch, **the language code**, the plan-guard verdict, and any
   review output from this session. It re-derives everything else from the repo. Dispatching it as a
   subagent is the point — it starts with no memory of writing the code, so it
   has nothing to defend, and its input-gathering never enters your context.
4. **Read the returned file** before publishing it. You are publishing content;
   you check it first. Verify the self-check list actually holds — 8 sections,
   no `{{` slots, section 7 non-empty, gates not optimistically green. On a
   non-English report also check that no identifier, violation code or gate name
   was translated.
5. **Publish** with the Artifact tool: the returned `TITLE` and `DESCRIPTION`
   (both already in the page's language — the reporter returns the description so
   the gallery card is not written twice, differently), favicon `📐` on first
   publish (omit it on every redeploy). Load the `artifact-design` skill first, as
   its own rules require.
6. **Write the PR body** — the reporter's `PR-BODY` block, **always English**,
   with the artifact URL on a `Full report:` line. Five to eight lines. The page
   carries the detail; the body is a pointer, not a second copy of it.

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

## Language

The report page is written for **one reader**, so it is written in his language
when he asks for it. The PR body is not: it sits on GitHub beside English
commits, English docs and CodeRabbit's own review, and a contributor or a bot
reading the PR is not the maintainer. **So the page and its artifact title follow
the requested language; the PR body, the commit messages and the issue text stay
English.**

`references/labels.sk.md` holds the Slovak chrome strings as a **fixed** table.
Translating them per report would undo the thing the fixed template buys — the
maintainer never re-orienting — so the glossary is the authority and a new
report does not get to improve on it. Adding a language means adding a
`references/labels.<code>.md` beside it, not loosening this rule.

What a translated report must **not** touch is the same list every time, and it
is in that file: identifiers, the literals the product emits (violation codes,
problem-type slugs, config and descriptor keys), section 2's code blocks, gate
names and their output, and the titles of specs, plans and issues. A page that
translates `read-only-required-field` is not a Slovak report, it is a wrong one.

## When it is not worth it

A docs-only PR, a dependabot bump, a one-line CI fix: skip the page, write a
normal short PR body. The report earns its cost when the PR changes what Alvo
can do.
