docker compose down --volumes
cp website/src/snippets/audit-row-changes/01-audit.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
