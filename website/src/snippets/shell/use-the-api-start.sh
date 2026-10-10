docker compose -f docker-compose.quickstart.yml down --volumes
unset ALVO_DESCRIPTOR
docker compose -f docker-compose.quickstart.yml up --wait --wait-timeout 90
