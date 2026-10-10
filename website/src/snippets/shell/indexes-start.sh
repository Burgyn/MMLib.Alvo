docker compose down --volumes
cp website/src/snippets/indexes/01-indexes.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
