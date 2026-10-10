docker compose down --volumes
curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/apply-and-evolve/01-base.alvo.json
docker compose up --wait --wait-timeout 90
