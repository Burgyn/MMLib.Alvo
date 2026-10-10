KEY="agent.$FLEET_DESK_KEY_SECRET"
OWNER=$(curl -s -X POST localhost:5199/api/alvo/owners -H "X-Alvo-Api-Key: $KEY" \
  -H "Content-Type: application/json" -d '{"name":"Fleet Desk Ltd"}' | jq -r .id)
VEHICLE=$(curl -s -X POST localhost:5199/api/alvo/vehicles -H "X-Alvo-Api-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"vin":"TMBJJ7NE8L0123456","plate":"BA-101AA","make":"Skoda","model":"Octavia","year":2020,"owner_id":"'"$OWNER"'"}' \
  | jq -r .id)
curl -s -c inspector.cookies -X POST localhost:5199/app/login \
  -H "Content-Type: application/json" -d '{"user":"inspector"}'
curl -s -c clerk.cookies -X POST localhost:5199/app/login \
  -H "Content-Type: application/json" -d '{"user":"clerk"}'
curl -s -b inspector.cookies -X PATCH "localhost:5199/app/vehicles/$VEHICLE" \
  -H "Content-Type: application/json" -d '{"color":"red"}' -w ' %{http_code}\n'
curl -s -b clerk.cookies -X PATCH "localhost:5199/app/vehicles/$VEHICLE" \
  -H "Content-Type: application/json" -d '{"color":"blue"}' -w ' %{http_code}\n'
