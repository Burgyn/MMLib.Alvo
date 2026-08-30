---
description: Build the visual 8-section PR report (published Artifact) for a PR number, branch, or the current branch
argument-hint: "[pr-number | branch] [sk|en]  (default: current branch, English)"
allowed-tools: Agent, Read, Bash(gh pr view:*), Bash(gh pr diff:*), Bash(gh pr checks:*), Bash(git log:*), Bash(git diff:*), Artifact
---

Build the Alvo PR report for: **$ARGUMENTS** (empty → the current branch against `main`).

Split the arguments first: a bare argument is a language code when it is `en` or when
`.claude/skills/alvo-pr-report/references/labels.<it>.md` exists — **defaulting to
`en`**; whatever remains is the PR number or branch. So `174 sk`, `sk` and `174` are
all unambiguous, and a new language needs only its labels file. A code with no labels
file is not treated as a branch name: say so and ask.

Follow the `alvo-pr-report` skill (`.claude/skills/alvo-pr-report/SKILL.md`) end to
end: dispatch the `alvo-pr-reporter` subagent with an output path of
`<scratchpad>/pr-report-<number-or-branch>.html` **and the language code**, read the
file it returns before publishing, publish it with the Artifact tool, and hand back
the URL together with the PR body text.

The language never enters the output path — a report regenerated in another language
replaces the page rather than forking it. So if this is a regeneration of a report
that already exists, reuse the same output path so the Artifact redeploys to the same
URL, and omit `favicon`.

The page and its title follow the requested language; the PR body stays English.
