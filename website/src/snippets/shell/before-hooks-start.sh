docker compose down --volumes
cp website/src/snippets/before-hooks/01-reject.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
