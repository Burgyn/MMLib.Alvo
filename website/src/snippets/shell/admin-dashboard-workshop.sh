docker compose -f docker-compose.quickstart.yml down --volumes
ALVO_DESCRIPTOR=/alvo/examples/bike-workshop/bike-workshop.alvo.json \
  docker compose -f docker-compose.quickstart.yml up --wait --wait-timeout 90
