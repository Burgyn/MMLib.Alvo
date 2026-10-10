git clone https://github.com/Burgyn/MMLib.Alvo && cd MMLib.Alvo
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up --build --wait --wait-timeout 60
curl -s localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET" \
  -H "Content-Type: application/json" -d '{"name":"Ada Lovelace"}'
curl -s localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET"
