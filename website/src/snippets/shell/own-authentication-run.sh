export FLEET_DESK_KEY_SECRET="$(openssl rand -hex 16)"
Alvo__Auth__DevKeys__0__Secret="$FLEET_DESK_KEY_SECRET" \
  dotnet run --project samples/MMLib.Alvo.Samples.EmbeddedHost -- \
  --FleetDesk:DatabasePath "$(mktemp -d)/fleet-desk.db" &
until curl -sf localhost:5199/health/ready > /dev/null; do sleep 1; done
