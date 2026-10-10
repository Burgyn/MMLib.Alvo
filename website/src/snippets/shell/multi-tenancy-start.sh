curl -fsSL -o docker-compose.override.yml https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/shell/multi-tenancy.compose.override.yml
export ALVO_ACME_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_GLOBEX_KEY_SECRET="$(openssl rand -hex 16)"
docker compose down --volumes
curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/multi-tenancy/01-tenancy.alvo.json
docker compose up --wait --wait-timeout 90
