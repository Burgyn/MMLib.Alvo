export ALVO_DESCRIPTOR="$PWD/examples/help-desk/help-desk.alvo.json"
export ALVO_AGENT_KEY_SECRET="$(openssl rand -hex 16)"
export ALVO_ADMIN_KEY_SECRET="$(openssl rand -hex 16)"
openssl rand -base64 24 > .alvo-admin-password
dotnet run --project src/MMLib.Alvo.Host -- \
  --environment Development --urls http://127.0.0.1:8080 \
  --Alvo:DescriptorPath="$ALVO_DESCRIPTOR" \
  --Alvo:Database:Provider=sqlite \
  "--Alvo:Database:SqliteConnectionString=Data Source=${TMPDIR:-/tmp}/alvo-help-desk.db" \
  --Alvo:Admin:BootstrapEmail=admin@example.com \
  --Alvo:Admin:BootstrapPasswordFile="$PWD/.alvo-admin-password" \
  --Alvo:Auth:DevKeys:0:KeyId=agent --Alvo:Auth:DevKeys:0:Secret="$ALVO_AGENT_KEY_SECRET" \
  --Alvo:Auth:DevKeys:0:User=3f2b8c1e-7a4d-4e9b-9c21-5d6e7f8a9b01 \
  --Alvo:Auth:DevKeys:0:Roles:0=agent --Alvo:Auth:DevKeys:0:Roles:1=authenticated \
  "--Alvo:Auth:DevKeys:0:Scopes:0=*:read" "--Alvo:Auth:DevKeys:0:Scopes:1=*:write" \
  --Alvo:Auth:DevKeys:1:KeyId=admin --Alvo:Auth:DevKeys:1:Secret="$ALVO_ADMIN_KEY_SECRET" \
  --Alvo:Auth:DevKeys:1:User=8d4e2a6c-1b3f-4c5d-8e7a-9f0b1c2d3e02 \
  --Alvo:Auth:DevKeys:1:Roles:0=admin --Alvo:Auth:DevKeys:1:Roles:1=authenticated \
  "--Alvo:Auth:DevKeys:1:Scopes:0=*:read" "--Alvo:Auth:DevKeys:1:Scopes:1=*:write"
