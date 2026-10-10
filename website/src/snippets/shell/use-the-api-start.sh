docker compose down --volumes
unset ALVO_DESCRIPTOR
docker compose up --build --wait --wait-timeout 60
