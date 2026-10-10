curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/apply-and-evolve/02-add-field.alvo.json
REVISION="$(curl -sS localhost:8080/management/projects/help-desk/descriptor \
  -H "X-Alvo-Api-Key: admin.$ALVO_ADMIN_KEY_SECRET" | jq -r .revision)"
jq -n --rawfile d help-desk.alvo.json '{descriptorJson: $d, reason: "Sort tickets by category."}' \
  | curl -sS -X PUT localhost:8080/management/projects/help-desk/descriptor \
      -H "X-Alvo-Api-Key: admin.$ALVO_ADMIN_KEY_SECRET" \
      -H "If-Match: \"$REVISION\"" \
      -H "Idempotency-Key: help-desk-add-category" \
      -H "Content-Type: application/json" \
      -d @-
