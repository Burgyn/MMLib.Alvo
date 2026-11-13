curl -si -X POST "$URL/api/alvo/orders" \
  -H "Content-Type: application/json" -H "X-Alvo-Api-Key: clerk.$SECRET" \
  -d '{"customer":"Ada","postcode":"040 01"}'
