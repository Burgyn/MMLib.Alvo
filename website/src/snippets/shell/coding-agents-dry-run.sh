curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/coding-agents/02-add-field.alvo.json
REVISION="$(curl -sS localhost:8080/management/projects/help-desk/descriptor \
  -H "X-Alvo-Api-Key: admin.$ALVO_ADMIN_KEY_SECRET" | jq -r .revision)"
jq -n --rawfile d help-desk.alvo.json '{descriptorJson: $d}' \
  | curl -sS -X PUT "localhost:8080/management/projects/help-desk/descriptor?dryRun=true" \
      -H "X-Alvo-Api-Key: admin.$ALVO_ADMIN_KEY_SECRET" \
      -H "If-Match: \"$REVISION\"" \
      -H "Content-Type: application/json" \
      -d @-
