cp website/src/snippets/coding-agents/01-base.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up -d --wait --force-recreate alvo
