docker compose down --volumes
cp website/src/snippets/apply-and-evolve/01-base.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
