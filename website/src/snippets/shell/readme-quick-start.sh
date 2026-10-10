curl -fsSLO https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/docker-compose.quickstart.yml
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_ADMIN_PASSWORD="$(openssl rand -hex 12)"
docker compose -f docker-compose.quickstart.yml up --wait --wait-timeout 90
curl -sS localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET" \
  -H "Content-Type: application/json" -d '{"name":"Ada Lovelace"}'
curl -sS localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET"
