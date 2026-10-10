cp website/src/snippets/tutorial/01-entity.alvo.json help-desk.alvo.json
cp website/src/snippets/shell/help-desk.compose.override.yml docker-compose.override.yml
openssl rand -base64 24 > .alvo-admin-password
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_AGENT_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_ADMIN_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
