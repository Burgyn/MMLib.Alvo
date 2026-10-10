dotnet user-secrets --project samples/MMLib.Alvo.Samples.EmbeddedHost \
  set "Alvo:Auth:DevKeys:0:Secret" "$(openssl rand -hex 16)"
dotnet run --project samples/MMLib.Alvo.Samples.EmbeddedHost
