FEED="${ALVO_FEED:-$HOME/alvo-feed}"
CLONE="${ALVO_CLONE:-$HOME/MMLib.Alvo}"
(cd "$CLONE" && dotnet pack -c Release -o "$FEED")
dotnet new nugetconfig
dotnet nuget add source "$FEED" -n alvo-local --configfile nuget.config
dotnet add package MMLib.Alvo --prerelease
dotnet add package MMLib.Alvo.Data.Sqlite --prerelease
