curl -fsSL -o help-desk.alvo.json https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/website/src/snippets/before-hooks/02-mutate.alvo.json
docker compose up -d --wait --force-recreate alvo
