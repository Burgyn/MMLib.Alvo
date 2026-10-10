KEY="agent.$FLEET_DESK_KEY_SECRET"
OWNER=$(curl -s -X POST localhost:5199/api/alvo/owners -H "X-Alvo-Api-Key: $KEY" \
  -H "Content-Type: application/json" -d '{"name":"Fleet Desk Ltd"}' | jq -r .id)
curl -s -X POST localhost:5199/api/alvo/vehicles -H "X-Alvo-Api-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"vin":"1hgcm82633a004352","plate":"BA-777AB","make":"Skoda","model":"Fabia","year":2020,"owner_id":"'"$OWNER"'"}' \
  | jq '{vin, plate}'
curl -s -X POST localhost:5199/api/alvo/vehicles -H "X-Alvo-Api-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"vin":"1hg-cm826-33a-004352","plate":"BA-778AB","make":"Skoda","model":"Fabia","year":2020,"owner_id":"'"$OWNER"'"}' \
  | jq '{status, violations}'
kill %1
