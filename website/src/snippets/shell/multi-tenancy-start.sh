cp website/src/snippets/shell/multi-tenancy.compose.override.yml docker-compose.override.yml
export ALVO_ACME_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_GLOBEX_KEY_SECRET="$(openssl rand -hex 16)"
docker compose down --volumes
cp website/src/snippets/multi-tenancy/01-tenancy.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
