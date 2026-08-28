---
description: Build the visual 8-section PR report (published Artifact) for a PR number, branch, or the current branch
argument-hint: "[pr-number | branch]  (default: current branch)"
allowed-tools: Agent, Read, Bash(gh pr view:*), Bash(gh pr diff:*), Bash(git log:*), Bash(git diff:*), Artifact
---

Build the Alvo PR report for: **$ARGUMENTS** (empty → the current branch against `main`).

Follow the `alvo-pr-report` skill (`.claude/skills/alvo-pr-report/SKILL.md`) end to
end: dispatch the `alvo-pr-reporter` subagent with an output path of
`<scratchpad>/pr-report-<number-or-branch>.html`, read the file it returns before
publishing, publish it with the Artifact tool, and hand back the URL together with
the PR body text.

If this is a regeneration of a report that already exists, reuse the same output
path so the Artifact redeploys to the same URL, and omit `favicon`.
