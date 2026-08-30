---
description: Build the visual 8-section PR report (published Artifact) for a PR number, branch, or the current branch
argument-hint: "[pr-number | branch] [sk|en]  (default: current branch, English)"
allowed-tools: Agent, Read, Bash(gh pr view:*), Bash(gh pr diff:*), Bash(gh pr checks:*), Bash(git log:*), Bash(git diff:*), Artifact
---

Build the Alvo PR report for: **$ARGUMENTS** (empty → the current branch against `main`).

Split the arguments first. A **language token is exactly two lowercase ASCII letters**
(`^[a-z]{2}$`), which is what keeps `174`, `f4/pr-a-apply-guards` and `feature/foo`
from ever being read as one. An argument that is not a language token is the PR number
or branch. A token that is `en`, or that has a
`.claude/skills/alvo-pr-report/references/labels.<token>.md`, selects that language —
**`en` is the default** when no token is given. A token with no labels file stops the
command: say the language is not available and list the ones that are, rather than
falling back to English or treating it as a branch, because a typo'd `sl` for `sk` is
far likelier than a two-letter branch name.

So `174 sk`, `sk` and `174` are all unambiguous, and a new language needs only its
labels file.

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
