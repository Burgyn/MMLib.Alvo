docker compose down --volumes
curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/audit-row-changes/01-audit.alvo.json
docker compose up --wait --wait-timeout 90
