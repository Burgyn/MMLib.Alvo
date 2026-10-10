FEED="${ALVO_FEED:-$HOME/alvo-feed}"
CLONE="${ALVO_CLONE:-$HOME/MMLib.Alvo}"
(cd "$CLONE" && dotnet pack -c Release -o "$FEED")
dotnet add package MMLib.Alvo --source "$FEED" --prerelease
dotnet add package MMLib.Alvo.Data.Sqlite --source "$FEED" --prerelease
