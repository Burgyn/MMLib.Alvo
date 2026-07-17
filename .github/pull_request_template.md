<!-- Title should follow Conventional Commits, e.g. "feat(data): add …". -->

## What & why

<!-- One or two sentences. Link the issue this implements. -->
Closes #

## Definition of done

- [ ] `scripts/test-ring2` green locally (bash + PowerShell)
- [ ] `dotnet format --verify-no-changes` clean
- [ ] Public API baseline updated if the public surface changed (or confirmed unchanged)
- [ ] Design + plan added/updated under `docs/superpowers/`

## Security core

- [ ] This change does **not** touch the rule engine, CEL, tenancy, or auth/RBAC
- [ ] If it does: labelled `needs-deep-review`, and ran `/security-review` + the `alvo-security-core-review` checklist

## Before requesting review

- [ ] Dispatched `alvo-plan-guard` (no blocking findings)
