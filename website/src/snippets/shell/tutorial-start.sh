mkdir -p alvo-help-desk && cd alvo-help-desk
curl -fsSLO https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/docker-compose.quickstart.yml
curl -fsSL -o docker-compose.override.yml https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/shell/help-desk.compose.override.yml
curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/tutorial/01-entity.alvo.json
export COMPOSE_FILE=docker-compose.quickstart.yml:docker-compose.override.yml
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)" ALVO_ADMIN_PASSWORD="$(openssl rand -hex 12)"
export ALVO_AGENT_KEY_SECRET="$(openssl rand -hex 16)" ALVO_ADMIN_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up --wait --wait-timeout 90
