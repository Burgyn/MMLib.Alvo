cp website/src/snippets/shell/production.compose.override.yml docker-compose.override.yml
openssl rand -base64 24 > .alvo-admin-password
openssl rand -base64 32 > .alvo-encryption-key
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up --build --wait --wait-timeout 60
