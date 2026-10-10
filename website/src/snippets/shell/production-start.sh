mkdir -p alvo-production && cd alvo-production
curl -fsSLO https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/docker-compose.quickstart.yml
curl -fsSL -o docker-compose.production.yml https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/shell/production.compose.override.yml
openssl rand -base64 32 > .alvo-encryption-key
export COMPOSE_FILE=docker-compose.quickstart.yml:docker-compose.production.yml
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)" ALVO_ADMIN_PASSWORD="$(openssl rand -hex 12)"
docker compose up --wait --wait-timeout 90
