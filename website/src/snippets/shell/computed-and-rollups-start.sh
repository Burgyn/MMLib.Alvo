docker compose down --volumes
cp website/src/snippets/computed-and-rollups/01-computed.alvo.json help-desk.alvo.json
export ALVO_DESCRIPTOR=./help-desk.alvo.json
docker compose up --build --wait --wait-timeout 60
