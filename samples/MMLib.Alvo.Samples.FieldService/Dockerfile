# syntax=docker/dockerfile:1

# The field-service embedded host's image: the advanced demo's second layer, run by
# docker-compose.field-service-host.yml and scripts/test-e2e. Never published — image.yml ships the standalone host only.
#
# The build context is the repository ROOT, for the reason src/MMLib.Alvo.Host/Dockerfile gives: Central Package
# Management, Directory.Build.props and .editorconfig live there, and a project built without them is a different build
# from the one CI gates. The shape is that Dockerfile's two stages, cut to what this sample references.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source

ARG VERSION=0.0.0-docker

# HUSKY=0 here is the IMAGE build: the context carries no .git, so there is no hook to install. Commits keep theirs.
ENV MinVerVersionOverride=$VERSION \
    EnableSourceLink=false \
    EnableSourceControlManagerQueries=false \
    HUSKY=0

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig MMLib.Alvo.slnx ./
# MMLib.Alvo compiles project.schema.json as an AdditionalFile; a context without it does not build.
COPY schema/ schema/
COPY src/ src/
COPY samples/Directory.Build.props samples/
COPY samples/MMLib.Alvo.Samples.FieldService/ samples/MMLib.Alvo.Samples.FieldService/
# The typed-entity generator reads the descriptor at BUILD time: the sample's WorkOrder, Visit and roles come from it.
COPY examples/field-service/field-service.alvo.json examples/field-service/

RUN dotnet restore samples/MMLib.Alvo.Samples.FieldService/MMLib.Alvo.Samples.FieldService.csproj
RUN dotnet publish samples/MMLib.Alvo.Samples.FieldService/MMLib.Alvo.Samples.FieldService.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

# The IANA time zone database: the sequence's, the schedule's and the service hours' Europe/Bratislava need it (#479).
RUN apk add --no-cache tzdata

WORKDIR /app
COPY --from=build /app .

# The same descriptor the build generated from, at the path the compose file names. Root-owned and read-only to the
# process, like a mounted `:ro` file.
COPY examples/field-service/field-service.alvo.json /alvo/descriptor.json

USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "MMLib.Alvo.Samples.FieldService.dll"]
