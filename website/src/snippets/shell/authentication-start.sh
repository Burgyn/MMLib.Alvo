curl -fsSL -o docker-compose.override.yml https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/shell/authentication.compose.override.yml
export ALVO_READER_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up -d --wait --force-recreate alvo
