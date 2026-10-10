cp website/src/snippets/shell/authentication.compose.override.yml docker-compose.override.yml
export ALVO_READER_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up -d --wait --force-recreate alvo
