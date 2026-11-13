dotnet new classlib -o DeliveryDesk.Tests && cd DeliveryDesk.Tests
dotnet add package xunit.v3
CLONE="${ALVO_CLONE:-$HOME/MMLib.Alvo}"
dotnet add reference "$CLONE/src/MMLib.Alvo.TestHost/MMLib.Alvo.TestHost.csproj"
dotnet add reference ../DeliveryDesk/DeliveryDesk.csproj
