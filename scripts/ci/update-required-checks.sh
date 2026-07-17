#!/usr/bin/env bash
#
# Set the required status checks on the `main` branch ruleset.
#
# Admin-only: needs `gh auth` with a token that can administer the repository.
# Idempotent — safe to re-run. Preview without changing anything: pass --dry-run.
# Re-run this whenever a new required CI check is added.
#
#   scripts/ci/update-required-checks.sh            # apply
#   scripts/ci/update-required-checks.sh --dry-run  # preview only
#
set -euo pipefail

repo="${ALVO_REPO:-Burgyn/MMLib.Alvo}"
ruleset_id="${ALVO_RULESET_ID:-18886535}"
required_checks=(
  "Build & test"
  "Brief freshness"
  "dependency-review"
  "Analyze (csharp)"
)

dry_run=false
if [ "${1:-}" = "--dry-run" ]; then
  dry_run=true
fi

contexts="$(printf '%s\n' "${required_checks[@]}" | jq -R '{context: .}' | jq -s '.')"
current="$(gh api "repos/$repo/rulesets/$ruleset_id")"
payload="$(jq --argjson contexts "$contexts" '
  {
    name,
    target,
    enforcement,
    conditions,
    bypass_actors: (.bypass_actors // []),
    rules: (.rules | map(
      if .type == "required_status_checks"
      then (.parameters.required_status_checks = $contexts)
      else . end))
  }' <<<"$current")"

echo "Ruleset $ruleset_id ($repo) required status checks →"
printf '  - %s\n' "${required_checks[@]}"

if [ "$dry_run" = true ]; then
  echo "--- dry-run, PUT payload (required_status_checks rule) ---"
  jq '.rules[] | select(.type == "required_status_checks")' <<<"$payload"
  exit 0
fi

jq -c '.' <<<"$payload" | gh api -X PUT "repos/$repo/rulesets/$ruleset_id" --input - >/dev/null
echo "Applied."
