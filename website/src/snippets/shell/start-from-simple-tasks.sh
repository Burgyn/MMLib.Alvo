docker compose down --volumes
cp examples/simple-tasks/tasks.alvo.json my.alvo.json
export ALVO_DESCRIPTOR=./my.alvo.json
docker compose up --build --wait --wait-timeout 60
curl -sS localhost:8080/api/projects -H "X-Alvo-Api-Key: admin.$ALVO_ADMIN_KEY_SECRET"
