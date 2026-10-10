docker compose down --volumes
curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/examples/simple-tasks/tasks.alvo.json
docker compose up --wait --wait-timeout 90
curl -sS localhost:8080/api/projects -H "X-Alvo-Api-Key: admin.$ALVO_ADMIN_KEY_SECRET"
