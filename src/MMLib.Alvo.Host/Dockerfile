# syntax=docker/dockerfile:1

# The build context is the repository ROOT, not this directory: Central Package Management,
# Directory.Build.props and .editorconfig live there, and a project built without them is a
# different build from the one CI gates. docker-compose.yml sets `context: .` for that reason.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source

# What the image build says about itself. MinVer derives a version from git tags and the context
# carries no .git, so the version is stated rather than discovered: F4's publish pipeline passes the
# real one, and until then the image says it has none instead of inheriting the SDK's 1.0.0 — a
# version this pre-1.0 project has never released.
ARG VERSION=0.0.0-docker

# The three things the repository's build looks for outside the context and would otherwise warn
# about, told rather than discovered. None of them relaxes a bar: TreatWarningsAsErrors still
# applies to every project here, and source-link metadata serves symbol servers for published
# packages, which this image is not. Set as environment so they reach restore and every referenced
# project, not only the one named on the command line.
ENV MinVerVersionOverride=$VERSION \
    EnableSourceLink=false \
    EnableSourceControlManagerQueries=false \
    HUSKY=0

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig MMLib.Alvo.slnx ./
# schema/ is not documentation here: MMLib.Alvo compiles project.schema.json as an AdditionalFile
# and the descriptor validator's types are generated from it, so a context without it does not build.
COPY schema/ schema/
COPY src/ src/

RUN dotnet restore src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj
RUN dotnet publish src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app .

# /alvo is where the descriptor is mounted and where the SQLite default would be written, so it has
# to exist and be writable by the non-root user the image runs as.
RUN mkdir -p /alvo/data && chown -R $APP_UID:$APP_UID /alvo
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "MMLib.Alvo.Host.dll"]
