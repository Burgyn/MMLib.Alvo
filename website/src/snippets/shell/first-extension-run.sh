SECRET=$(openssl rand -hex 16)
dotnet user-secrets init
dotnet user-secrets set "Alvo:Auth:DevKeys:0:Secret" "$SECRET"
dotnet run
